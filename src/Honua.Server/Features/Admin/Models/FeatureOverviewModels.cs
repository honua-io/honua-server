// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for the feature overview page.
/// </summary>
public sealed class FeatureOverviewResponse
{
    /// <summary>
    /// Current platform edition name.
    /// </summary>
    public required string CurrentEdition { get; init; }

    /// <summary>
    /// All platform features with their enabled/disabled state.
    /// </summary>
    public required FeatureOverviewItem[] Features { get; init; }

    /// <summary>
    /// The full unified capability roster (ADR-0058, Decision B) projected from
    /// <see cref="Honua.Core.Features.Capabilities.ICapabilityRegistry"/>, resolved
    /// for the current edition/environment through the T2 gate resolver so operators
    /// can see every capability and <b>why</b> each is on or off. Read-only — there
    /// is deliberately no mutable toggle endpoint (the flag design defers writes to
    /// the config-bound experimental switches).
    /// </summary>
    public required CapabilityOverviewItem[] Capabilities { get; init; }
}

/// <summary>
/// One capability from the unified registry, projected with its resolved
/// enabled/reason state for the current edition and environment.
/// </summary>
public sealed class CapabilityOverviewItem
{
    /// <summary>
    /// Stable, unique capability identifier (for example <c>format.geoparquet</c>
    /// or <c>protocol.mcp.tool.plan_analysis</c>).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The broad kind of capability (<c>Feature</c>, <c>ProtocolOperation</c>, or
    /// <c>DataFormat</c>).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The product-lifecycle maturity (<c>Planned</c>, <c>Deferred</c>,
    /// <c>Experimental</c>, <c>Partial</c>, or <c>Implemented</c>).
    /// </summary>
    public required string Maturity { get; init; }

    /// <summary>
    /// Whether the capability resolves enabled for the current edition/environment.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// The stable machine-readable reason the capability is disabled (for example
    /// <c>experimental-disabled</c> or <c>license-required</c>), or <c>null</c> when
    /// the capability is enabled.
    /// </summary>
    public string? ReasonCode { get; init; }
}

/// <summary>
/// Individual feature item with edition-gated status.
/// </summary>
public sealed class FeatureOverviewItem
{
    /// <summary>
    /// Unique feature key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Feature category for grouping.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Brief description of the feature.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether the feature is enabled under the current edition.
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>
    /// Minimum edition required to enable this feature.
    /// </summary>
    public required string MinimumEdition { get; init; }

    /// <summary>
    /// Upgrade message if the feature is not enabled, null if enabled.
    /// </summary>
    public string? UpgradeMessage { get; init; }
}
