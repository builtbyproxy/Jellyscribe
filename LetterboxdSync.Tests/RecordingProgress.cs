using System;
using System.Collections.Generic;

namespace LetterboxdSync.Tests;

/// <summary>
/// Records every progress report inline. <see cref="Progress{T}"/> posts its callback to the
/// thread pool, so a test reading its values straight after the run can miss the last report.
/// </summary>
internal sealed class RecordingProgress : IProgress<double>
{
    private readonly object _lock = new();
    private readonly List<double> _values = new();

    public IReadOnlyList<double> Values
    {
        get { lock (_lock) return _values.ToArray(); }
    }

    public void Report(double value)
    {
        lock (_lock) _values.Add(value);
    }
}
