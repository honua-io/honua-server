// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Diagnostic emitted by the OGC coverage import pipeline describing a coverage
/// style hint (color map, band statistics, NoData marker, transparency preset,
/// legend, or rendering hint) that could not be transferred verbatim with the
/// raw pixels. Slice 4 of issue #1030.
/// </summary>
/// <remarks>
/// Coverage *style* migration is deliberately separate from coverage *data*
/// migration: pixels can be downloaded and re-registered, but per-band lookup
/// tables, NoData markers, legend swatches and renderer-specific transforms
/// have to be recreated against the target rendering pipeline. These records
/// surface every hint discovered from the source so operators can decide which
/// to recreate automatically, which to copy verbatim, and which to rebuild by
/// hand.
/// </remarks>
public sealed record MigrationCoverageStyleDiagnostic
{
    /// <summary>
    /// Style kind. One of: <c>colorMap</c>, <c>bandStatistics</c>,
    /// <c>noDataValue</c>, <c>transparency</c>, <c>legend</c>, <c>renderingHint</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Migration classification: <c>automated</c>, <c>assisted</c>, or
    /// <c>manual-review</c>. Determines how the diagnostic must be acted on
    /// in the downstream apply stage.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Source coverage identifier the diagnostic applies to.
    /// </summary>
    public required string SourceCoverageId { get; init; }

    /// <summary>
    /// Optional source style identifier (for example a GeoServer SLD name or
    /// an OGC API Coverages <c>/styles/{styleId}</c> document) when the
    /// diagnostic was derived from a named style entry.
    /// </summary>
    public string? SourceStyleId { get; init; }

    /// <summary>
    /// Human-readable reason describing why the diagnostic was emitted.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Stable suggestion for the target style identifier when the diagnostic
    /// classifier could match the source hint to an existing Honua style
    /// preset (e.g. <c>grayscale-linear-stretch</c>). Null when no preset
    /// match is available and operators must author a target style manually.
    /// </summary>
    public string? SuggestedTargetStyleId { get; init; }

    /// <summary>
    /// Vendor name for vendor-specific extensions (e.g. <c>Esri</c>,
    /// <c>GeoServer</c>). Null when the diagnostic does not originate from a
    /// recognised vendor extension marker.
    /// </summary>
    public string? VendorName { get; init; }

    /// <summary>
    /// Manual steps an operator must perform to reconcile this style hint
    /// against the target. Empty for fully automated diagnostics.
    /// </summary>
    public string[] ManualSteps { get; init; } = [];
}
