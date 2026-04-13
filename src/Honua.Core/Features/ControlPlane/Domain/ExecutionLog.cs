// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Severity levels for structured execution log entries.
/// </summary>
public enum ExecutionLogLevel
{
    /// <summary>
    /// Verbose diagnostic information.
    /// </summary>
    Debug,

    /// <summary>
    /// Informational progress messages.
    /// </summary>
    Info,

    /// <summary>
    /// Non-fatal conditions that may require attention.
    /// </summary>
    Warning,

    /// <summary>
    /// Errors that may not halt execution but indicate problems.
    /// </summary>
    Error
}

/// <summary>
/// A single structured log entry produced during execution job processing.
/// Workers append entries through <see cref="Abstractions.IExecutionLogStore"/>;
/// API callers read them for diagnostics and audit.
/// </summary>
public sealed record ExecutionLogEntry
{
    /// <summary>
    /// UTC timestamp when the entry was recorded.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Severity level for this entry.
    /// </summary>
    public required ExecutionLogLevel Level { get; init; }

    /// <summary>
    /// Human-readable log message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Execution phase active when this entry was recorded (e.g., "Provisioning", "Step 3/5").
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Optional structured metadata for machine-readable diagnostics.
    /// Keys and values are opaque strings to preserve AOT compatibility.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
