// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Sdk;

/// <summary>Reports coarse progress back to the job. The default impl logs.</summary>
public interface IProgressReporter
{
    /// <summary>Report coarse progress.</summary>
    /// <param name="percent">A percentage in <c>[0, 100]</c> (clamped by the harness).</param>
    /// <param name="phase">A short human-readable phase label.</param>
    void Report(double percent, string phase);
}

/// <summary>
/// Structured-ish logger surface for tools. Lines are captured as job logs. Kept
/// dependency-free so a tool's own logging setup cannot suppress harness/job lines.
/// </summary>
public interface IGpLogger
{
    /// <summary>Log an informational line.</summary>
    /// <param name="message">The message.</param>
    void Info(string message);

    /// <summary>Log a warning line.</summary>
    /// <param name="message">The message.</param>
    void Warn(string message);
}
