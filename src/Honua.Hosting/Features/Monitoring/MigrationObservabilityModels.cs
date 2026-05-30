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
    /// Timestamp when the response was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}
