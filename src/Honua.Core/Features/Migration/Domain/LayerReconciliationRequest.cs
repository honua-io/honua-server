// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Per-run input to <see cref="Abstractions.ILayerReconciliationService"/>. Carries the
/// per-layer source-side snapshot collected during apply so the service does not have to
/// re-issue source HTTP calls (which would risk drift-induced false positives).
/// </summary>
public sealed record LayerReconciliationRequest
{
    /// <summary>
    /// Stable identifier for the originating migration run.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Source kind that produced the layers being reconciled, for example
    /// <c>geoserver-rest</c> or <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Per-layer reconciliation inputs. Empty when the apply produced no
    /// successfully published layers (the artifact still records the run with no probes).
    /// </summary>
    public IReadOnlyList<LayerReconciliationLayerInput> Layers { get; init; } = [];

    /// <summary>
    /// Tolerances and sampling options. Defaults preserve the proven
    /// <c>reconcile.ts</c> behavior with conservative count/geometry bands.
    /// </summary>
    public LayerReconciliationOptions Options { get; init; } = new();
}

/// <summary>
/// Single-layer reconciliation input.
/// </summary>
public sealed record LayerReconciliationLayerInput
{
    /// <summary>
    /// Stable source-side identifier, for example a GeoServer
    /// <c>workspace:layer</c> id or an ArcGIS resource id. Used in the artifact
    /// to correlate per-layer results back to source intent.
    /// </summary>
    public required string SourceLayerId { get; init; }

    /// <summary>
    /// Human-readable source layer label. Falls back to <see cref="SourceLayerId"/>
    /// when the upstream pipeline does not carry a separate display name.
    /// </summary>
    public string? SourceLayerName { get; init; }

    /// <summary>
    /// Honua catalog layer id assigned by publish. Reconciliation is skipped when this is
    /// <c>null</c> (the apply did not produce a queryable layer for this source).
    /// </summary>
    public required int? TargetHonuaLayerId { get; init; }

    /// <summary>
    /// Source-side feature count snapshot recorded at apply time. <c>null</c> when the
    /// source did not advertise a count; the count probe will record a <c>warn</c> with
    /// a "no baseline" reason.
    /// </summary>
    public long? SourceFeatureCount { get; init; }

    /// <summary>
    /// Source-side bounding box snapshot. <c>null</c> when the source did not advertise an
    /// extent; the extent probe will record a <c>warn</c> with a "no baseline" reason.
    /// </summary>
    public BoundingBox? SourceExtent { get; init; }

    /// <summary>
    /// Source-advertised field names (deduplicated). Empty when the source did not advertise
    /// any schema; the content probe will record <c>pass</c> with a "no baseline" note.
    /// </summary>
    public IReadOnlyList<string> SourceFieldNames { get; init; } = [];

    /// <summary>
    /// Optional secret-safe filter mirror applied at apply time (e.g. CQL or GeoServices
    /// <c>where</c>) so the count probe can be filter-mirrored. <c>null</c> means the apply
    /// imported all features and the probe issues an unfiltered count.
    /// </summary>
    public string? FilterMirror { get; init; }
}

/// <summary>
/// Tunable tolerances applied across every probe. Defaults mirror the proven
/// <c>reconcile.ts</c> bands and the ArcGIS parity runner's conservative thresholds.
/// </summary>
public sealed record LayerReconciliationOptions
{
    /// <summary>
    /// Default sample size for the geometry-validity and content probes.
    /// </summary>
    public const int DefaultSampleSize = 100;

    /// <summary>
    /// Default count delta-ratio at or below which the count probe records <c>pass</c>.
    /// </summary>
    public const double DefaultCountWarnRatio = 0.05;

    /// <summary>
    /// Default count delta-ratio above which the count probe records <c>fail</c>.
    /// </summary>
    public const double DefaultCountFailRatio = 0.20;

    /// <summary>
    /// Default geometry validity ratio at or above which the geometry probe records <c>pass</c>.
    /// </summary>
    public const double DefaultGeometryPassRatio = 0.99;

    /// <summary>
    /// Default geometry validity ratio below the pass band but at or above which the geometry
    /// probe records <c>warn</c> (rather than <c>fail</c>).
    /// </summary>
    public const double DefaultGeometryWarnRatio = 0.95;

    /// <summary>
    /// Default extent dimension tolerance, expressed as a ratio of the source dimension. Allows
    /// for sub-meter reprojection / rounding noise around an otherwise identical bbox.
    /// </summary>
    public const double DefaultExtentTolerance = 0.001;

    /// <summary>
    /// Sample size used for geometry validity and attribute schema probes. Clamped to <c>[1, 10000]</c>.
    /// </summary>
    public int SampleSize { get; init; } = DefaultSampleSize;

    /// <summary>
    /// Count delta-ratio at or below which the count probe records <c>pass</c>.
    /// </summary>
    public double CountWarnRatio { get; init; } = DefaultCountWarnRatio;

    /// <summary>
    /// Count delta-ratio above which the count probe records <c>fail</c>.
    /// </summary>
    public double CountFailRatio { get; init; } = DefaultCountFailRatio;

    /// <summary>
    /// Geometry validity ratio at or above which the geometry probe records <c>pass</c>.
    /// </summary>
    public double GeometryPassRatio { get; init; } = DefaultGeometryPassRatio;

    /// <summary>
    /// Geometry validity ratio at or above which the geometry probe records <c>warn</c>
    /// (when it is below <see cref="GeometryPassRatio"/>).
    /// </summary>
    public double GeometryWarnRatio { get; init; } = DefaultGeometryWarnRatio;

    /// <summary>
    /// Extent dimension tolerance, expressed as a ratio of the source dimension. Smaller
    /// deltas are treated as projection / rounding noise and recorded as <c>pass</c>.
    /// </summary>
    public double ExtentTolerance { get; init; } = DefaultExtentTolerance;
}
