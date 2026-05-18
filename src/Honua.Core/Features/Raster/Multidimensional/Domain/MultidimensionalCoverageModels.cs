// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Domain;

/// <summary>
/// Source format for a cloud-optimized multidimensional coverage.
/// Each value selects a different on-disk container layout. The shared
/// chunk/range read contract is the same; only the metadata extractor differs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MultidimensionalCoverageFormat>))]
public enum MultidimensionalCoverageFormat
{
    /// <summary>
    /// Cloud-optimized HDF5 (.h5/.hdf5) with chunked datasets and
    /// CF-style coordinate variables. Group browsing unrelated to geospatial
    /// coverage is not supported.
    /// </summary>
    CloudOptimizedHdf5 = 1,

    /// <summary>
    /// NetCDF-4 (.nc/.nc4), the HDF5-backed enhanced NetCDF data model.
    /// Coordinate discovery follows CF-1.x conventions.
    /// </summary>
    NetCdf4 = 2
}

/// <summary>
/// Persisted catalog entry for a cloud-hosted multidimensional coverage source
/// (HDF5 or NetCDF4). Carries everything required to range-read the object
/// once a reader is enabled, plus operator-declared variable selection.
/// </summary>
public sealed record MultidimensionalCoverageRegistration
{
    /// <summary>
    /// Unique registration identifier.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Layer this coverage source is associated with.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Human-readable name for the registration.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Container format of the registered object.
    /// </summary>
    public required MultidimensionalCoverageFormat Format { get; init; }

    /// <summary>
    /// Cloud storage provider hosting the object.
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Operator-declared variable names that should be exposed as coverage
    /// fields. Empty array means "expose every CF-compliant data variable
    /// discovered during the next metadata scan".
    /// </summary>
    public required IReadOnlyList<string> Variables { get; init; }

    /// <summary>
    /// Parsed coverage metadata. Null until a metadata scan has succeeded.
    /// </summary>
    public MultidimensionalCoverageMetadata? Metadata { get; init; }

    /// <summary>
    /// When metadata was last scanned from the cloud object.
    /// </summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>
    /// Registration timestamp.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Request to register a cloud-hosted multidimensional coverage source.
/// </summary>
public sealed record MultidimensionalCoverageRegistrationRequest
{
    /// <summary>
    /// Layer to associate the coverage with.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Container format.
    /// </summary>
    public required MultidimensionalCoverageFormat Format { get; init; }

    /// <summary>
    /// Cloud storage provider.
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Operator-declared variable names. Empty means "all data variables".
    /// </summary>
    public required IReadOnlyList<string> Variables { get; init; }
}

/// <summary>
/// Discovered metadata for a multidimensional coverage source: CF-compliant
/// data variables, dimensions, CRS, spatial/temporal/vertical extent, and the
/// chunk layout that range-efficient reads rely on.
/// </summary>
public sealed record MultidimensionalCoverageMetadata
{
    /// <summary>
    /// Container format the metadata was extracted from.
    /// </summary>
    public required MultidimensionalCoverageFormat Format { get; init; }

    /// <summary>
    /// CRS as an EPSG SRID. 0 when the source does not declare a CF
    /// <c>grid_mapping</c> attribute that can be resolved.
    /// </summary>
    public required int Srid { get; init; }

    /// <summary>
    /// Spatial extent in the declared CRS. Null when no horizontal coordinate
    /// variables were discovered (rare, but possible for time-only series).
    /// </summary>
    public RasterExtent? Extent { get; init; }

    /// <summary>
    /// Pixel/cell resolution in CRS units along (x, y). Zero entries when
    /// the source uses irregular coordinate spacing.
    /// </summary>
    public (double X, double Y) Resolution { get; init; }

    /// <summary>
    /// Temporal extent, when a CF time coordinate is present.
    /// </summary>
    public TemporalExtent? Temporal { get; init; }

    /// <summary>
    /// Vertical extent (pressure level / depth / altitude), when a CF
    /// vertical coordinate is present.
    /// </summary>
    public VerticalExtent? Vertical { get; init; }

    /// <summary>
    /// Discovered data variables. Empty when the operator-declared variable
    /// names did not match any data variable in the source.
    /// </summary>
    public required IReadOnlyList<MultidimensionalCoverageVariable> Variables { get; init; }
}

/// <summary>
/// One CF-compliant data variable exposed as a coverage field.
/// </summary>
public sealed record MultidimensionalCoverageVariable(
    string Name,
    string DataType,
    IReadOnlyList<MultidimensionalCoverageDimension> Dimensions,
    MultidimensionalChunkLayout? ChunkLayout,
    string? Units,
    string? LongName,
    string? StandardName,
    double? NoData);

/// <summary>
/// One named dimension of a coverage variable.
/// </summary>
public sealed record MultidimensionalCoverageDimension(string Name, long Size);

/// <summary>
/// Chunk layout for a variable. Required for range-efficient reads.
/// </summary>
public sealed record MultidimensionalChunkLayout(
    IReadOnlyList<long> ChunkShape,
    string Compression,
    bool ShuffleFilter);

/// <summary>
/// Temporal extent of a coverage source.
/// </summary>
public sealed record TemporalExtent(DateTimeOffset Start, DateTimeOffset End, long StepCount);

/// <summary>
/// Vertical extent of a coverage source.
/// </summary>
public sealed record VerticalExtent(double Min, double Max, long StepCount, string Units);
