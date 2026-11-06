// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<MultidimensionalCoverageFormat>))]
public enum MultidimensionalCoverageFormat
{
    CloudOptimizedHdf5 = 1,

    NetCdf4 = 2,

    Grib = 3
}

public sealed record MultidimensionalCoverageRegistration
{
    public required long Id { get; init; }

    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required MultidimensionalCoverageFormat Format { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string ObjectKey { get; init; }

    public required IReadOnlyList<string> Variables { get; init; }

    public MultidimensionalCoverageMetadata? Metadata { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record MultidimensionalCoverageRegistrationRequest
{
    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required MultidimensionalCoverageFormat Format { get; init; }

    public required CloudStorageProvider Provider { get; init; }

    public required string Bucket { get; init; }

    public required string ObjectKey { get; init; }

    public required IReadOnlyList<string> Variables { get; init; }
}

public sealed record MultidimensionalCoverageMetadata
{
    public required MultidimensionalCoverageFormat Format { get; init; }

    public required int Srid { get; init; }

    public RasterExtent? Extent { get; init; }

    public (double X, double Y) Resolution { get; init; }

    public TemporalExtent? Temporal { get; init; }

    public VerticalExtent? Vertical { get; init; }

    public bool YAxisAscending { get; init; }

    public bool HasYAxisOrientation { get; init; }

    public required IReadOnlyList<MultidimensionalCoverageVariable> Variables { get; init; }

    public CoverageSampleStatistics? Statistics { get; init; }
}

public sealed record MultidimensionalCoverageVariable(
    string Name,
    string DataType,
    IReadOnlyList<MultidimensionalCoverageDimension> Dimensions,
    MultidimensionalChunkLayout? ChunkLayout,
    string? Units,
    string? LongName,
    string? StandardName,
    double? NoData);

public enum MultidimensionalAxisKind
{
    Unknown = 0,

    HorizontalX = 1,

    HorizontalY = 2,

    Temporal = 3,

    Vertical = 4,
}

public sealed record MultidimensionalCoverageDimension(string Name, long Size)
{
    public MultidimensionalAxisKind Axis { get; init; }
}

public sealed record CoverageSampleStatistics(
    double Minimum,
    double Maximum,
    double? Mean,
    double? StandardDeviation);

public sealed record MultidimensionalChunkLayout(
    IReadOnlyList<long> ChunkShape,
    string Compression,
    bool ShuffleFilter);

public sealed record TemporalExtent(DateTimeOffset Start, DateTimeOffset End, long StepCount);

public sealed record VerticalExtent(double Min, double Max, long StepCount, string Units);
