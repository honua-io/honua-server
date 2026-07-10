// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Instance-scoped migration observability payload for the admin control plane.
/// </summary>
internal sealed record MigrationObservabilityResponse
{
    /// <summary>
    /// Current migration lifecycle state for this Honua instance.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Whether the instance is currently ready for traffic.
    /// </summary>
    public required bool IsReady { get; init; }

    /// <summary>
    /// Whether the current migration lifecycle state is failed.
    /// </summary>
    public required bool IsFailed { get; init; }

    /// <summary>
    /// Optional operator-facing detail about the current lifecycle state.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether a point-in-time migration plan could be generated.
    /// </summary>
    public bool PlanAvailable { get; init; }

    /// <summary>
    /// Whether the instance currently has pending migrations.
    /// </summary>
    public bool UpgradeRequired { get; init; }

    /// <summary>
    /// Migration scripts that would run if upgrades were applied now.
    /// </summary>
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Executed scripts that are no longer discovered by the current binary.
    /// </summary>
    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error detail when the plan could not be generated.
    /// </summary>
    public string? PlanError { get; init; }

    /// <summary>
    /// Last backup-hook outcome for the current pending contract-migration set, when applicable.
    /// </summary>
    public MigrationBackupHookStatus? BackupHook { get; init; }

    /// <summary>
    /// Timestamp when the response was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Backup-hook status surfaced on migration/preflight admin payloads.
/// </summary>
public sealed class MigrationBackupHookStatus
{
    /// <summary>
    /// Whether <c>Database:MigrationSafety:BackupCommand</c> is configured for this instance.
    /// </summary>
    public bool Configured { get; init; }

    /// <summary>
    /// Whether the backup hook is relevant to the current pending contract-migration set.
    /// </summary>
    public bool RequiredForPendingSet { get; init; }

    /// <summary>
    /// Whether the last recorded hook outcome matches the current pending contract-migration set.
    /// </summary>
    public bool RanForPendingSet { get; init; }

    /// <summary>
    /// Whether the matching hook run succeeded. Null when no matching run has been recorded.
    /// </summary>
    public bool? Succeeded { get; init; }

    /// <summary>
    /// Stable lower-case outcome label, for example <c>succeeded</c> or <c>failed</c>.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// Hook process start time for the matching outcome.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Hook completion time for the matching outcome.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Hook wall-clock duration in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Process exit code for the matching outcome, when available.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Truncated stderr for a failed matching outcome.
    /// </summary>
    public string? Stderr { get; init; }

    /// <summary>
    /// Contract-phase scripts in the current pending set.
    /// </summary>
    public IReadOnlyList<string> PendingContractScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Best-effort migration run identifier associated with the matching outcome.
    /// </summary>
    public string? MigrationRunId { get; init; }

    /// <summary>
    /// Correlation id associated with the matching outcome.
    /// </summary>
    public string? CorrelationId { get; init; }
}
