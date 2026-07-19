using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Startup task that registers with File Transformation plugin to inject
/// the Letterboxd sidebar link into index.html for all users.
/// </summary>
/// <remarks>
/// Registration races File Transformation's own startup init: both plugins fire their
/// StartupTrigger tasks independently, and Jellyfin gives no ordering guarantee between
/// them. When File Transformation hasn't finished initializing yet, <c>RegisterTransformation</c>
/// throws (observed in production as an intermittent, restart-dependent failure - same code,
/// same plugin version, works on some boots and not others). A single attempt made this a
/// coin flip on every Jellyfin restart, including the weekly scheduled VM reboot, with no
/// user-visible signal that it happened. <see cref="ExecuteAsync"/> retries a few times with a
/// short delay before giving up, and unwraps <see cref="TargetInvocationException"/> so a real
/// failure logs the actual cause instead of reflection's generic wrapper message.
/// </remarks>
public class SidebarInjectionTask : IScheduledTask
{
    /// <summary>Total attempts before giving up (1 initial + retries).</summary>
    internal const int MaxAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<SidebarInjectionTask> _logger;

    public string Name => "Jellyscribe Sidebar Registration";

    public string Key => "LetterboxdSidebarInjection";

    public string Description => "Registers sidebar link with File Transformation plugin.";

    public string Category => "Jellyscribe";

    public SidebarInjectionTask(ILogger<SidebarInjectionTask> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Test-only override for the registration attempt. When set, the retry loop calls this
    /// instead of the real reflection-based <see cref="RegisterWithFileTransformation"/>, so
    /// tests can simulate N failures before success (or persistent failure) without a real
    /// File Transformation assembly loaded. Production never assigns it.
    /// </summary>
    internal Action? RegisterAttemptOverride;

    /// <summary>
    /// Test-only override for the inter-attempt delay. When set, replaces the real
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> wait so retry tests run instantly
    /// instead of waiting <see cref="RetryDelay"/> in real time. Production never assigns it.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? DelayOverride;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(10);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (RegisterAttemptOverride != null)
                    RegisterAttemptOverride();
                else
                    RegisterWithFileTransformation();

                _logger.LogInformation("Letterboxd sidebar injection registered successfully");
                break;
            }
            catch (Exception ex)
            {
                // TargetInvocationException.Message is always the generic reflection
                // boilerplate ("Exception has been thrown by the target of an invocation.");
                // the real cause is the InnerException.
                var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

                if (attempt == MaxAttempts)
                {
                    _logger.LogWarning(real, "Sidebar injection failed after {Attempts} attempt(s): {Error}", attempt, real.Message);
                    break;
                }

                _logger.LogDebug(real, "Sidebar injection attempt {Attempt}/{MaxAttempts} failed, retrying in {Delay}",
                    attempt, MaxAttempts, RetryDelay);

                var delay = DelayOverride ?? Task.Delay;
                await delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        progress.Report(100);
    }

    private void RegisterWithFileTransformation()
    {
        Assembly? ftAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);

        if (ftAssembly == null)
        {
            ftAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);
        }

        if (ftAssembly == null)
        {
            _logger.LogDebug("File Transformation plugin not installed, skipping sidebar injection");
            return;
        }

        Type? pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterface == null)
        {
            _logger.LogWarning("File Transformation PluginInterface type not found");
            return;
        }

        var registerMethod = pluginInterface.GetMethod("RegisterTransformation");
        if (registerMethod == null)
        {
            _logger.LogWarning("RegisterTransformation method not found");
            return;
        }

        // Build the JObject using File Transformation's own Newtonsoft assembly
        // to avoid type identity mismatch between different loaded copies
        Assembly? newtonsoftAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json"
                && a != typeof(Newtonsoft.Json.Linq.JObject).Assembly);

        // Fall back to any loaded copy
        if (newtonsoftAssembly == null)
        {
            newtonsoftAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json");
        }

        if (newtonsoftAssembly == null)
        {
            _logger.LogWarning("Newtonsoft.Json assembly not found");
            return;
        }

        Type? jObjectType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JObject");
        Type? jPropertyType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JProperty");

        if (jObjectType == null || jPropertyType == null)
        {
            _logger.LogWarning("Could not find JObject/JProperty types in Newtonsoft.Json");
            return;
        }

        var jObj = Activator.CreateInstance(jObjectType)!;
        var addMethod = jObjectType.GetMethod("Add", new[] { typeof(object) });

        void AddProp(string name, string value)
        {
            var prop = Activator.CreateInstance(jPropertyType, name, (object)value)!;
            addMethod!.Invoke(jObj, new[] { prop });
        }

        AddProp("id", Guid.NewGuid().ToString());
        AddProp("fileNamePattern", "index.html");
        AddProp("callbackAssembly", typeof(SidebarTransformCallback).Assembly.FullName!);
        AddProp("callbackClass", typeof(SidebarTransformCallback).FullName!);
        AddProp("callbackMethod", nameof(SidebarTransformCallback.Transform));

        _logger.LogInformation("Registering sidebar transformation with File Transformation plugin");
        registerMethod.Invoke(null, new[] { jObj });
    }
}

public static class SidebarTransformCallback
{
    public static string Transform(SidebarPatchPayload payload)
    {
        var contents = payload.Contents ?? string.Empty;

        // Only transform actual HTML files, not JS chunks with "index-html" in their name
        if (!contents.Contains("</head>") || contents.Contains("LetterboxdSync/Web/sidebar.js"))
        {
            return contents;
        }

        var injection = "<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script>";
        return contents.Replace("</head>", $"{injection}\n</head>");
    }
}

public class SidebarPatchPayload
{
    public string? Contents { get; set; }
}
