// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>
/// Tiny stderr logger so tool output is interleaved into job logs. Intentionally
/// dependency-free so a tool's own logging setup cannot suppress harness/job lines.
/// Mirrors the Python harness's <c>_StdLogger</c>.
/// </summary>
public sealed class HarnessLogger(TextWriter? writer = null) : IGpLogger
{
    private readonly TextWriter _writer = writer ?? Console.Error;

    /// <inheritdoc />
    public void Info(string message) => _writer.WriteLine($"[tool] INFO  {message}");

    /// <inheritdoc />
    public void Warn(string message) => _writer.WriteLine($"[tool] WARN  {message}");
}

/// <summary>A progress reporter that logs each report. Mirrors the Python default.</summary>
public sealed class LoggingProgressReporter(IGpLogger log) : IProgressReporter
{
    private readonly IGpLogger _log = log;

    /// <inheritdoc />
    public void Report(double percent, string phase)
    {
        var clamped = Math.Clamp(percent, 0.0, 100.0);
        _log.Info($"progress {clamped.ToString("F1", CultureInfo.InvariantCulture),5}% — {phase}");
    }
}
