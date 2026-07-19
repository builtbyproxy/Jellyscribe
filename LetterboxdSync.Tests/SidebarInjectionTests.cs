using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class SidebarInjectionTaskTests
{
    /// <summary>Captures log entries (including the passed exception) for assertions.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) { Entries.Add((logLevel, formatter(state, exception), exception)); }
        }
    }

    private static Func<TimeSpan, CancellationToken, Task> NoOpDelay(List<TimeSpan> recorded) =>
        (delay, _) => { lock (recorded) recorded.Add(delay); return Task.CompletedTask; };


    [Fact]
    public void Metadata_NameKeyDescriptionCategory_AreSet()
    {
        var task = new SidebarInjectionTask(NullLogger<SidebarInjectionTask>.Instance);

        Assert.Equal("Jellyscribe Sidebar Registration", task.Name);
        Assert.Equal("LetterboxdSidebarInjection", task.Key);
        Assert.False(string.IsNullOrEmpty(task.Description));
        Assert.Equal("Jellyscribe", task.Category);
    }

    [Fact]
    public void GetDefaultTriggers_IsStartupTrigger()
    {
        var task = new SidebarInjectionTask(NullLogger<SidebarInjectionTask>.Instance);

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.StartupTrigger, triggers[0].Type);
    }

    [Fact]
    public async Task ExecuteAsync_FileTransformationNotPresent_CompletesWithoutThrowing()
    {
        // Test environment doesn't load File Transformation, so the registration
        // logic falls into the early-exit "plugin not installed" debug-log path
        // and ExecuteAsync completes cleanly with progress 100.
        var task = new SidebarInjectionTask(NullLogger<SidebarInjectionTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        // No exception = pass.
    }

    [Fact]
    public async Task ExecuteAsync_ReportsProgressTo100()
    {
        var task = new SidebarInjectionTask(NullLogger<SidebarInjectionTask>.Instance);
        // Use a custom IProgress<T> impl that records synchronously instead of
        // Progress<T> which dispatches via SynchronizationContext (flaky under parallel test).
        var captured = new SynchronizedRecorder<double>();

        await task.ExecuteAsync(captured, CancellationToken.None);

        Assert.Contains(10.0, captured.Values);
        Assert.Contains(100.0, captured.Values);
    }

    /// <summary>
    /// Simple synchronous IProgress&lt;T&gt; recorder. Avoids the SynchronizationContext
    /// dispatch that Progress&lt;T&gt; does, which can race under xUnit's parallel runner.
    /// </summary>
    private class SynchronizedRecorder<T> : IProgress<T>
    {
        private readonly object _lock = new();
        private readonly List<T> _values = new();
        public IReadOnlyList<T> Values { get { lock (_lock) return _values.ToList(); } }
        public void Report(T value) { lock (_lock) _values.Add(value); }
    }

    [Fact]
    public async Task ExecuteAsync_RegisterSucceedsFirstTry_DoesNotRetry()
    {
        var logs = new ListLogger<SidebarInjectionTask>();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var task = new SidebarInjectionTask(logs)
        {
            RegisterAttemptOverride = () => attempts++,
            DelayOverride = NoOpDelay(delays),
        };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Empty(delays);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("registered successfully"));
    }

    [Fact]
    public async Task ExecuteAsync_RegisterFailsThenSucceeds_RetriesAndSucceeds()
    {
        var logs = new ListLogger<SidebarInjectionTask>();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var task = new SidebarInjectionTask(logs)
        {
            RegisterAttemptOverride = () =>
            {
                attempts++;
                if (attempts < SidebarInjectionTask.MaxAttempts)
                    throw new InvalidOperationException("File Transformation not ready yet");
            },
            DelayOverride = NoOpDelay(delays),
        };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(SidebarInjectionTask.MaxAttempts, attempts);
        Assert.Equal(SidebarInjectionTask.MaxAttempts - 1, delays.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("registered successfully"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_RegisterAlwaysFails_RetriesMaxAttemptsThenLogsWarning()
    {
        var logs = new ListLogger<SidebarInjectionTask>();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var task = new SidebarInjectionTask(logs)
        {
            RegisterAttemptOverride = () =>
            {
                attempts++;
                throw new InvalidOperationException("File Transformation still not ready");
            },
            DelayOverride = NoOpDelay(delays),
        };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(SidebarInjectionTask.MaxAttempts, attempts);
        Assert.Equal(SidebarInjectionTask.MaxAttempts - 1, delays.Count);

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains($"after {SidebarInjectionTask.MaxAttempts} attempt", warning.Message);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("registered successfully"));
    }

    [Fact]
    public async Task ExecuteAsync_UnwrapsTargetInvocationException_LogsRealInnerException()
    {
        // The production failure path goes through reflection (MethodInfo.Invoke), which
        // always wraps the real exception in a TargetInvocationException whose own .Message
        // is a generic, useless string. This is the class of bug that made the original
        // production failure undiagnosable without temporarily raising the log level.
        var logs = new ListLogger<SidebarInjectionTask>();
        var delays = new List<TimeSpan>();
        var inner = new NullReferenceException("File Transformation's internal registry wasn't initialized yet");
        var task = new SidebarInjectionTask(logs)
        {
            RegisterAttemptOverride = () => throw new TargetInvocationException(inner),
            DelayOverride = NoOpDelay(delays),
        };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Same(inner, warning.Exception);
        Assert.Contains(inner.Message, warning.Message);
        Assert.DoesNotContain("target of an invocation", warning.Message);
    }
}

public class SidebarTransformTests
{
    [Fact]
    public void Transform_InjectsScriptBeforeHeadClose()
    {
        var payload = new SidebarPatchPayload
        {
            Contents = "<html><head><title>Jellyfin</title></head><body></body></html>"
        };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Contains("<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script>", result);
        Assert.Contains("</head>", result);
        // Script should appear before </head>
        var scriptIdx = result.IndexOf("sidebar.js");
        var headIdx = result.IndexOf("</head>");
        Assert.True(scriptIdx < headIdx);
    }

    [Fact]
    public void Transform_DoesNotDoubleInject()
    {
        var payload = new SidebarPatchPayload
        {
            Contents = "<html><head><script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script></head><body></body></html>"
        };

        var result = SidebarTransformCallback.Transform(payload);

        // Should return content unchanged
        Assert.Equal(payload.Contents, result);
    }

    [Fact]
    public void Transform_SkipsNonHtmlContent()
    {
        var jsContent = "\"use strict\";(self.webpackChunk=self.webpackChunk||[]).push([[17244],{session-login-index-html:function(){}}]);";
        var payload = new SidebarPatchPayload { Contents = jsContent };

        var result = SidebarTransformCallback.Transform(payload);

        // JS content without </head> should pass through unchanged
        Assert.Equal(jsContent, result);
        Assert.DoesNotContain("sidebar.js", result);
    }

    [Fact]
    public void Transform_SkipsJsChunkWithIndexHtmlInName()
    {
        // This was the actual bug: JS chunks like "session-login-index-html.chunk.js"
        // were being matched and corrupted with HTML injection
        var jsContent = "(self.webpackChunk=self.webpackChunk||[]).push([[17244],{14999:function(t,e,a){a.r(e)}}]);";
        var payload = new SidebarPatchPayload { Contents = jsContent };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Equal(jsContent, result);
        Assert.DoesNotContain("<script", result);
    }

    [Fact]
    public void Transform_HandlesNullContents()
    {
        var payload = new SidebarPatchPayload { Contents = null };

        var result = SidebarTransformCallback.Transform(payload);

        // Should return empty string, not throw
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Transform_HandlesEmptyContents()
    {
        var payload = new SidebarPatchPayload { Contents = string.Empty };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Transform_PreservesExistingContent()
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Jellyfin</title><script src=\"/Moonfin/Web/loader.js\" defer></script></head><body><div id=\"reactRoot\"></div></body></html>";
        var payload = new SidebarPatchPayload { Contents = html };

        var result = SidebarTransformCallback.Transform(payload);

        // Original content should be preserved
        Assert.Contains("Moonfin/Web/loader.js", result);
        Assert.Contains("<meta charset=\"utf-8\">", result);
        Assert.Contains("<div id=\"reactRoot\">", result);
        // Our script should be added
        Assert.Contains("LetterboxdSync/Web/sidebar.js", result);
    }

    [Fact]
    public void Transform_RealWorldIndexHtml_InjectsCorrectly()
    {
        // Simulate the actual Jellyfin index.html structure
        var html = "<!doctype html><html class=\"preload\" dir=\"ltr\"><head><meta charset=\"utf-8\">"
            + "<script defer src=\"main.jellyfin.bundle.js\"></script>"
            + "<style>.headerMoonfinButton{opacity:.6}</style>"
            + "<script src=\"/Moonfin/Web/loader.js\" defer></script>"
            + "</head><body dir=\"ltr\"><div id=\"reactRoot\"></div></body></html>";

        var payload = new SidebarPatchPayload { Contents = html };

        var result = SidebarTransformCallback.Transform(payload);

        // Count script tags - should have original 2 + our 1
        var scriptCount = result.Split("<script").Length - 1;
        Assert.Equal(3, scriptCount);

        // Our injection should be right before </head>
        var sidebarIdx = result.IndexOf("LetterboxdSync/Web/sidebar.js");
        var headCloseIdx = result.IndexOf("</head>");
        Assert.True(sidebarIdx > 0);
        Assert.True(sidebarIdx < headCloseIdx);
    }
}

public class SidebarPatchPayloadTests
{
    [Fact]
    public void Contents_DefaultsToNull()
    {
        var payload = new SidebarPatchPayload();
        Assert.Null(payload.Contents);
    }

    [Fact]
    public void Contents_CanBeSetAndRead()
    {
        var payload = new SidebarPatchPayload { Contents = "test content" };
        Assert.Equal("test content", payload.Contents);
    }
}
