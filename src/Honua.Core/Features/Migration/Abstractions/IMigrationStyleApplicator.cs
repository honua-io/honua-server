// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Applies a successfully converted migration style to Honua's live, render-facing style catalogs.
/// </summary>
public interface IMigrationStyleApplicator
{
    /// <summary>
    /// Idempotently creates or updates the canonical style and assigns it to published target layers.
    /// </summary>
    /// <param name="request">Converted style and deterministic target-layer assignments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live catalog application outcome.</returns>
    Task<MigrationStyleApplyOutcome> ApplyAsync(
        MigrationLiveStyleApplyRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A converted migration style ready for application to the canonical style catalogs.
/// </summary>
public sealed record MigrationLiveStyleApplyRequest
{
    /// <summary>Stable canonical style identifier.</summary>
    public required string TargetStyleId { get; init; }

    /// <summary>Operator-facing style title.</summary>
    public required string Title { get; init; }

    /// <summary>Converted JSON array of MapLibre layer objects.</summary>
    public string? MapLibreLayersJson { get; init; }

    /// <summary>Evidence disposition; only <c>applied</c> styles may reach live storage.</summary>
    public required string ReviewDisposition { get; init; }

    /// <summary>Published target layers ordered by GeoServer style precedence.</summary>
    public IReadOnlyList<MigrationStyleLayerTarget> LayerTargets { get; init; } = [];
}

/// <summary>
/// A published target layer that references a migrated source style.
/// </summary>
/// <param name="LayerId">Canonical Honua layer identifier returned by publication.</param>
/// <param name="Ordinal">Style precedence for the layer; zero is the default style.</param>
public sealed record MigrationStyleLayerTarget(int LayerId, int Ordinal);

/// <summary>
/// Result of applying a migrated style to live render-facing storage.
/// </summary>
public enum MigrationStyleApplyOutcome
{
    /// <summary>The canonical style or at least one layer assignment was changed.</summary>
    Applied,

    /// <summary>The canonical style and all assignments already matched.</summary>
    AlreadyApplied,

    /// <summary>The conversion requires manual review and was deliberately not applied.</summary>
    SkippedManualReview,

    /// <summary>No successfully published target layer referenced the style.</summary>
    SkippedNoPublishedLayers
}
