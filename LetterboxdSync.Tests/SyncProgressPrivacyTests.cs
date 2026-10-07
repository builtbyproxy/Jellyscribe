using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// GET /Progress returns one process-wide snapshot that every signed-in user can read, so the
/// phase and task text a runner sets must never carry a Jellyfin or Letterboxd/Serializd
/// username, an email, or a film/episode title.
/// This scans every <c>SyncProgress.Start</c>/<c>SetPhase</c> call in the plugin source, so a
/// new runner that interpolates a name into its phase fails here rather than in production.
/// </summary>
public class SyncProgressPrivacyTests
{
    private static readonly Regex ProgressCall = new(
        @"SyncProgress\.(?:Start|SetPhase)\((?<args>[^;]*)\);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Any reference to a user, account or item name in the arguments, whether interpolated or
    // concatenated. Film and episode titles count too: they say what someone is watching.
    private static readonly Regex NameReference = new(
        @"(?:Username|DisplayName|Email|\.Name\b|Title\b|\btitle\b)",
        RegexOptions.Compiled);

    private static string PluginSourceDir([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "LetterboxdSync"));

    [Fact]
    public void NoProgressCallNamesAUserOrTitle()
    {
        var dir = PluginSourceDir();
        Assert.True(Directory.Exists(dir), $"plugin source not found at {dir}");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();

        var calls = 0;
        var offenders = files
            .SelectMany(f => ProgressCall.Matches(File.ReadAllText(f))
                .Select(m => (File: Path.GetFileName(f), Args: m.Groups["args"].Value)))
            .Select(c => { calls++; return c; })
            .Where(c => NameReference.IsMatch(c.Args))
            .Select(c => $"{c.File}: {c.Args.Trim()}")
            .ToList();

        Assert.True(calls > 10, $"expected to find the runners' progress calls, found {calls}");
        Assert.Empty(offenders);
    }
}
