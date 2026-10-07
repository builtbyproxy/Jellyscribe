using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using LetterboxdSync;
using LetterboxdSync.Serializd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Full-file rewrites must go to a temp file that is then renamed over the original, so a crash
/// mid-write leaves the old file intact. The tests hold a handle to the original across the
/// rewrite: a rename leaves that handle on the old content, an in-place rewrite does not.
/// </summary>
[Collection("Plugin")]
public class AtomicWriteTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "letterboxd.jsonl");
        SerializdActivity.DataPathOverride = Path.Combine(_tempDir, "serializd.jsonl");
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
    }

    public void Dispose()
    {
        SyncHistory.SetLogger(NullLogger.Instance);
        SerializdActivity.SetLogger(NullLogger.Instance);
        SyncHistory.DataPathOverride = null;
        SerializdActivity.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static string[] SkippedEvents(int count) => Enumerable.Range(0, count)
        .Select(i => JsonSerializer.Serialize(new SyncEvent
        {
            FilmTitle = "Film",
            TmdbId = 1,
            Username = "u",
            Timestamp = DateTime.UtcNow.AddMinutes(-i),
            Status = SyncStatus.Skipped,
            Error = "No TMDb ID",
        }))
        .ToArray();

    private static string ReadThroughHandle(FileStream handle)
    {
        handle.Position = 0;
        return new StreamReader(handle).ReadToEnd();
    }

    private static FileStream OpenOriginal(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public void SyncHistory_Rewrite_ReplacesTheFile()
    {
        var path = SyncHistory.DataPathOverride!;
        File.WriteAllLines(path, SkippedEvents(SyncHistory.MaxPrunableEventsPerFilm + 3));
        var original = File.ReadAllText(path);
        using var handle = OpenOriginal(path);

        SyncHistory.GetStats(); // load compacts, which rewrites the file

        Assert.Equal(original, ReadThroughHandle(handle));
        Assert.NotEqual(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SerializdActivity_Rewrite_ReplacesTheFile()
    {
        var path = SerializdActivity.DataPathOverride!;
        File.WriteAllLines(path, SkippedEvents(SyncHistory.MaxPrunableEventsPerFilm + 3));
        var original = File.ReadAllText(path);
        using var handle = OpenOriginal(path);

        SerializdActivity.GetStats();

        Assert.Equal(original, ReadThroughHandle(handle));
        Assert.NotEqual(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UnreadableLines_AreSkipped_WithOneWarningPerLoad()
    {
        var logger = new WarningCounter();
        SyncHistory.SetLogger(logger);
        SerializdActivity.SetLogger(logger);
        var lines = SkippedEvents(1).Concat(new[] { "{not json", "also not json" }).ToArray();
        File.WriteAllLines(SyncHistory.DataPathOverride!, lines);
        File.WriteAllLines(SerializdActivity.DataPathOverride!, lines);

        Assert.Equal(1, SyncHistory.GetStats().Total);
        Assert.Equal(1, SerializdActivity.GetStats().Total);

        Assert.Equal(2, logger.Warnings);
    }

    private sealed class WarningCounter : ILogger
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}
