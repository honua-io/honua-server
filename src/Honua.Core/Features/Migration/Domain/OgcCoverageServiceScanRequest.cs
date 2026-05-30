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
/// Structured request used to build an OGC coverage service migration inventory.
/// </summary>
public sealed record OgcCoverageServiceScanRequest
{
    /// <summary>
    /// Coverage protocol surface to scan. Supported values are <c>WCS</c> and <c>OGC API Coverages</c>.
    /// </summary>
    public required string ServiceType { get; init; }

    /// <summary>
    /// Source service URL used for migration planning.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional service version advertised by the source.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Service-level metadata captured from capabilities or landing-page documents.
    /// </summary>
    public OgcCoverageServiceMetadata ServiceMetadata { get; init; } = new();

    /// <summary>
    /// Coverage resources advertised by the source service.
    /// </summary>
    public OgcCoverageDescription[] Coverages { get; init; } = [];
}

/// <summary>
/// Service-level metadata for an OGC coverage source.
/// </summary>
public sealed record OgcCoverageServiceMetadata
{
    /// <summary>
    /// Human-readable service title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Service abstract or description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Source product or implementation name.
    /// </summary>
    public string? Product { get; init; }

    /// <summary>
    /// Provider or organization name advertised by the source.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Service fees or licensing statements.
    /// </summary>
    public string[] Fees { get; init; } = [];

    /// <summary>
    /// Access constraints advertised by the source.
    /// </summary>
    public string[] AccessConstraints { get; init; } = [];
}

/// <summary>
/// Coverage metadata captured for migration planning.
/// </summary>
public sealed record OgcCoverageDescription
{
    /// <summary>
    /// Stable source coverage identifier.
    /// </summary>
    public required string CoverageId { get; init; }

    /// <summary>
    /// Optional human-readable coverage title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional coverage abstract or description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Coverage type advertised by the source, such as <c>RectifiedGridCoverage</c>.
    /// </summary>
    public string? CoverageType { get; init; }

    /// <summary>
    /// Native source storage format when advertised.
    /// </summary>
    public string? NativeFormat { get; init; }

    /// <summary>
    /// Access constraints specific to this coverage.
    /// </summary>
    public string[] AccessConstraints { get; init; } = [];

    /// <summary>
    /// CRS values associated with the coverage.
    /// </summary>
    public OgcCoverageCrsMetadata[] Crs { get; init; } = [];

    /// <summary>
    /// Axis and subset metadata associated with the coverage domain.
    /// </summary>
    public OgcCoverageAxisMetadata[] Axes { get; init; } = [];

    /// <summary>
    /// Range, band, or field metadata associated with the coverage values.
    /// </summary>
    public OgcCoverageRangeMetadata[] Ranges { get; init; } = [];

    /// <summary>
    /// Output formats advertised for the coverage.
    /// </summary>
    public OgcCoverageFormatMetadata[] OutputFormats { get; init; } = [];

    /// <summary>
    /// Temporal dimensions advertised for the coverage.
    /// </summary>
    public OgcCoverageTemporalDimensionMetadata[] TemporalDimensions { get; init; } = [];
}

/// <summary>
/// CRS metadata for a coverage domain or output path.
/// </summary>
public sealed record OgcCoverageCrsMetadata
{
    /// <summary>
    /// Role of the CRS entry, such as <c>native</c>, <c>supported</c>, or <c>output</c>.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Source-provided CRS identifier or definition.
    /// </summary>
    public required string Crs { get; init; }

    /// <summary>
    /// Axis order advertised by the source for this CRS.
    /// </summary>
    public string? AxisOrder { get; init; }

    /// <summary>
    /// Axis labels associated with this CRS entry.
    /// </summary>
    public string[] AxisLabels { get; init; } = [];
}

/// <summary>
/// Axis and subset metadata for an OGC coverage domain.
/// </summary>
public sealed record OgcCoverageAxisMetadata
{
    /// <summary>
    /// Axis name or subset identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Axis type such as <c>x</c>, <c>y</c>, <c>time</c>, <c>elevation</c>, or <c>band</c>.
    /// </summary>
    public string? AxisType { get; init; }

    /// <summary>
    /// Source CRS axis label when distinct from the subset name.
    /// </summary>
    public string? CrsAxisLabel { get; init; }

    /// <summary>
    /// Axis unit of measure.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Lower domain bound advertised for the axis.
    /// </summary>
    public string? LowerBound { get; init; }

    /// <summary>
    /// Upper domain bound advertised for the axis.
    /// </summary>
    public string? UpperBound { get; init; }

    /// <summary>
    /// Axis resolution when available.
    /// </summary>
    public string? Resolution { get; init; }

    /// <summary>
    /// Whether the source explicitly allows subsetting by this axis.
    /// </summary>
    public bool? Subsettable { get; init; }

    /// <summary>
    /// Discrete values advertised for the axis.
    /// </summary>
    public string[] AllowedValues { get; init; } = [];

    /// <summary>
    /// Default axis value used by the source.
    /// </summary>
    public string? DefaultValue { get; init; }
}

/// <summary>
/// Range, band, or field metadata for an OGC coverage.
/// </summary>
public sealed record OgcCoverageRangeMetadata
{
    /// <summary>
    /// Range or band name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display label for the range or band.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Source data type, such as <c>UInt16</c> or <c>Float32</c>.
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>
    /// Unit of measure for range values.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// No-data value advertised for the range.
    /// </summary>
    public string? NoDataValue { get; init; }

    /// <summary>
    /// Range interpretation, such as <c>red</c>, <c>green</c>, <c>blue</c>, or <c>temperature</c>.
    /// </summary>
    public string? Interpretation { get; init; }

    /// <summary>
    /// Lower valid sample value when advertised.
    /// </summary>
    public string? MinimumValue { get; init; }

    /// <summary>
    /// Upper valid sample value when advertised.
    /// </summary>
    public string? MaximumValue { get; init; }
}

/// <summary>
/// Output format metadata advertised for an OGC coverage.
/// </summary>
public sealed record OgcCoverageFormatMetadata
{
    /// <summary>
    /// Source format token.
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Optional media type for the format.
    /// </summary>
    public string? MediaType { get; init; }

    /// <summary>
    /// Optional format profile, such as a Cloud Optimized GeoTIFF profile URI.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Whether this format is the native source encoding.
    /// </summary>
    public bool? IsNative { get; init; }
}

/// <summary>
/// Temporal dimension metadata advertised for an OGC coverage.
/// </summary>
public sealed record OgcCoverageTemporalDimensionMetadata
{
    /// <summary>
    /// Temporal dimension name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Start instant or date for the dimension extent.
    /// </summary>
    public string? Start { get; init; }

    /// <summary>
    /// End instant or date for the dimension extent.
    /// </summary>
    public string? End { get; init; }

    /// <summary>
    /// Default temporal value selected by the source.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Temporal resolution or interval when advertised.
    /// </summary>
    public string? Interval { get; init; }

    /// <summary>
    /// Discrete temporal values when advertised.
    /// </summary>
    public string[] Values { get; init; } = [];

    /// <summary>
    /// Whether the source explicitly allows subsetting by this temporal dimension.
    /// </summary>
    public bool? Subsettable { get; init; }
}
