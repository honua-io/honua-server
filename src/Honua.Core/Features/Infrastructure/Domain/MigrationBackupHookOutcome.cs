// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Structured outcome from a pre-migration backup hook execution.
/// </summary>
public sealed record MigrationBackupHookOutcome
{
    /// <summary>
    /// Correlation identifier for the migration run that executed the backup hook.
    /// </summary>
    public required string MigrationRunId { get; init; }

    /// <summary>
    /// Stable fingerprint of the pending contract-script set the hook protected.
    /// </summary>
    public required string PendingSetFingerprint { get; init; }

    /// <summary>
    /// Pending contract-phase scripts present when the hook ran.
    /// </summary>
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the backup hook completed successfully.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Hook duration in milliseconds.
    /// </summary>
    public required long DurationMs { get; init; }

    /// <summary>
    /// UTC timestamp when the hook process was launched.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the hook outcome was recorded.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Process exit code when the hook process started and exited.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Sanitized and truncated stderr for failed hook executions.
    /// </summary>
    public string? TruncatedStderr { get; init; }
}
