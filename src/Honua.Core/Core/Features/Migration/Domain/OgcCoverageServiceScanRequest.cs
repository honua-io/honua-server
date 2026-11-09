// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcCoverageServiceScanRequest
{
    public required string ServiceType { get; init; }

    public required string ServiceUrl { get; init; }

    public string? Version { get; init; }

    public OgcCoverageServiceMetadata ServiceMetadata { get; init; } = new();

    public OgcCoverageDescription[] Coverages { get; init; } = [];
}

public sealed record OgcCoverageServiceMetadata
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? Product { get; init; }

    public string? ProviderName { get; init; }

    public string[] Fees { get; init; } = [];

    public string[] AccessConstraints { get; init; } = [];
}

public sealed record OgcCoverageDescription
{
    public required string CoverageId { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? CoverageType { get; init; }

    public string? NativeFormat { get; init; }

    public string[] AccessConstraints { get; init; } = [];

    public OgcCoverageCrsMetadata[] Crs { get; init; } = [];

    public OgcCoverageAxisMetadata[] Axes { get; init; } = [];

    public OgcCoverageRangeMetadata[] Ranges { get; init; } = [];

    public OgcCoverageFormatMetadata[] OutputFormats { get; init; } = [];

    public OgcCoverageTemporalDimensionMetadata[] TemporalDimensions { get; init; } = [];
}

public sealed record OgcCoverageCrsMetadata
{
    public required string Role { get; init; }

    public required string Crs { get; init; }

    public string? AxisOrder { get; init; }

    public string[] AxisLabels { get; init; } = [];
}

public sealed record OgcCoverageAxisMetadata
{
    public required string Name { get; init; }

    public string? AxisType { get; init; }

    public string? CrsAxisLabel { get; init; }

    public string? Unit { get; init; }

    public string? LowerBound { get; init; }

    public string? UpperBound { get; init; }

    public string? Resolution { get; init; }

    public bool? Subsettable { get; init; }

    public string[] AllowedValues { get; init; } = [];

    public string? DefaultValue { get; init; }
}

public sealed record OgcCoverageRangeMetadata
{
    public required string Name { get; init; }

    public string? Label { get; init; }

    public string? DataType { get; init; }

    public string? Unit { get; init; }

    public string? NoDataValue { get; init; }

    public string? Interpretation { get; init; }

    public string? MinimumValue { get; init; }

    public string? MaximumValue { get; init; }
}

public sealed record OgcCoverageFormatMetadata
{
    public required string Format { get; init; }

    public string? MediaType { get; init; }

    public string? Profile { get; init; }

    public bool? IsNative { get; init; }
}

public sealed record OgcCoverageTemporalDimensionMetadata
{
    public required string Name { get; init; }

    public string? Start { get; init; }

    public string? End { get; init; }

    public string? DefaultValue { get; init; }

    public string? Interval { get; init; }

    public string[] Values { get; init; } = [];

    public bool? Subsettable { get; init; }
}
