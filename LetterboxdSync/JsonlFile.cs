using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LetterboxdSync;

/// <summary>
/// Write helpers for the plugin's line-per-record stores (sync history, Serializd activity and
/// history, the rating push store).
/// </summary>
internal static class JsonlFile
{
    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

    /// <summary>
    /// Appends one line. If the file does not end in a newline (a crash cut the last append short),
    /// a newline goes first, so the new record never runs into the broken one and is lost with it.
    /// </summary>
    public static void AppendLine(string path, string line)
    {
        EnsureDirectory(path);
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length > 0)
        {
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n')
                fs.Write(NewLine);
        }

        fs.Seek(0, SeekOrigin.End);
        fs.Write(Encoding.UTF8.GetBytes(line));
        fs.Write(NewLine);
    }

    /// <summary>
    /// Replaces the file with <paramref name="lines"/>: written beside it, flushed to disk, then
    /// moved over it, so a crash or power loss mid-write leaves the old file intact.
    /// </summary>
    public static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
    {
        EnsureDirectory(path);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            foreach (var line in lines)
                writer.WriteLine(line);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Same as <see cref="WriteAllLinesAtomic"/> for a single text blob.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        EnsureDirectory(path);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(new UTF8Encoding(false).GetBytes(text));
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
