// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Outcome captured when the optional database migration backup hook runs.
/// </summary>
public sealed record DatabaseMigrationBackupHookResult
{
    /// <summary>
    /// Stable lower-case outcome label, for example <c>succeeded</c> or <c>failed</c>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Whether the backup hook completed successfully.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// UTC timestamp when the hook process was started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the hook outcome was known.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Hook wall-clock duration in milliseconds.
    /// </summary>
    public required long DurationMilliseconds { get; init; }

    /// <summary>
    /// Process exit code, when the process launched and exited normally.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Truncated stderr captured from a failed hook. Null for successful hooks or failures without
    /// stderr.
    /// </summary>
    public string? Stderr { get; init; }

    /// <summary>
    /// Contract-phase scripts guarded by this backup hook invocation.
    /// </summary>
    public IReadOnlyList<string> PendingContractScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Best-effort schema migration run identifier used to correlate audit/timeline rows with the
    /// migration attempt.
    /// </summary>
    public string? MigrationRunId { get; init; }

    /// <summary>
    /// Correlation identifier copied to audit and timeline surfaces.
    /// </summary>
    public string? CorrelationId { get; init; }
}
