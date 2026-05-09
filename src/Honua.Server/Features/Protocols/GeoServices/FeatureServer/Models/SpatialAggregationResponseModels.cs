// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

internal static class SpatialAggregationContractConstants
{
    public const string ResultSchemaVersion = "honua.spatial-aggregation.v1";
    public const string MetadataSchemaVersion = "honua.spatial-aggregation.metadata.v1";
    public const string Capability = "spatialAggregate";
    public const string FeatureServerProtocol = "geoservices-feature-service";
    public const string H3IndexModelId = "h3";
}

internal sealed class SpatialAggregationResultResponse
{
    public string SchemaVersion { get; init; } = SpatialAggregationContractConstants.ResultSchemaVersion;

    public string? RequestId { get; init; }

    public required string SourceId { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public required SpatialAggregationIndexStateResponse Index { get; init; }

    public required SpatialAggregationMetadataResponse Metadata { get; init; }

    public required SpatialAggregationCellResponse[] Cells { get; init; }

    public Dictionary<string, SpatialAggregationSummaryValueResponse>? Totals { get; init; }

    public SpatialAggregationGroupedSummaryResponse[]? Groups { get; init; }

    public SpatialAggregationPageInfoResponse? Page { get; init; }

    public SpatialAggregationDegradedReasonResponse[]? Degraded { get; init; }
}

internal sealed class SpatialAggregationIndexStateResponse
{
    public required SpatialAggregationIndexModelResponse Model { get; init; }

    public int? Resolution { get; init; }

    public int? CellCount { get; init; }
}

internal sealed class SpatialAggregationIndexModelResponse
{
    public required string Id { get; init; }

    public string? Title { get; init; }

    public string? Family { get; init; }

    public string? CellIdEncoding { get; init; }

    public int? MinResolution { get; init; }

    public int? MaxResolution { get; init; }

    public string[]? SupportedGeometry { get; init; }

    public string? Hierarchy { get; init; }

    public GeoServicesSpatialReference? SpatialReference { get; init; }
}

internal sealed class SpatialAggregationMetadataResponse
{
    public string SchemaVersion { get; init; } = SpatialAggregationContractConstants.MetadataSchemaVersion;

    public string? SourceId { get; init; }

    public SpatialAggregationIndexModelResponse[]? IndexModels { get; init; }

    public required SpatialAggregationSummaryMetadataResponse[] Summaries { get; init; }

    public SpatialAggregationGroupByMetadataResponse[]? GroupBy { get; init; }

    public SpatialAggregationWidgetMetadataResponse[]? Widgets { get; init; }

    public SpatialAggregationProgressiveStateResponse? Progressive { get; init; }

    public SpatialAggregationCacheMetadataResponse? Cache { get; init; }
}

internal sealed class SpatialAggregationSummaryMetadataResponse
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? Field { get; init; }

    public string? ValueType { get; init; }

    public string? Unit { get; init; }

    public SpatialAggregationRangeBucketMetadataResponse[]? Ranges { get; init; }

    public SpatialAggregationHistogramMetadataResponse? Histogram { get; init; }
}

internal sealed class SpatialAggregationRangeBucketMetadataResponse
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool? IncludeMin { get; init; }

    public bool? IncludeMax { get; init; }
}

internal sealed class SpatialAggregationHistogramMetadataResponse
{
    public int? Bins { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public string? Method { get; init; }
}

internal sealed class SpatialAggregationGroupByMetadataResponse
{
    public required string Field { get; init; }

    public string? Alias { get; init; }

    public string? Title { get; init; }

    public string? ValueType { get; init; }
}

internal sealed class SpatialAggregationWidgetMetadataResponse
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? SummaryId { get; init; }

    public string[]? SummaryIds { get; init; }

    public string? Field { get; init; }

    public string[]? GroupBy { get; init; }

    public string? ValueType { get; init; }

    public string? Unit { get; init; }

    public string[]? Interactions { get; init; }

    public SpatialAggregationWidgetProgressiveResponse? Progressive { get; init; }
}

internal sealed class SpatialAggregationWidgetProgressiveResponse
{
    public bool StableAcrossPages { get; init; }

    public required string PartialValueSemantics { get; init; }
}

internal sealed class SpatialAggregationProgressiveStateResponse
{
    public required string Status { get; init; }

    public string? Refinement { get; init; }

    public int? LoadedCellCount { get; init; }

    public int? TotalCellCount { get; init; }
}

internal sealed class SpatialAggregationCacheMetadataResponse
{
    public bool MetadataCacheable { get; init; }

    public bool ResultCacheable { get; init; }

    public string[]? CacheKeyParts { get; init; }

    public int? TtlMs { get; init; }
}

internal sealed class SpatialAggregationCellResponse
{
    public required string Id { get; init; }

    public int? Resolution { get; init; }

    public JsonElement? Geometry { get; init; }

    public required Dictionary<string, SpatialAggregationSummaryValueResponse> Summaries { get; init; }

    public SpatialAggregationGroupedSummaryResponse[]? Groups { get; init; }

    public bool? Partial { get; init; }
}

internal sealed class SpatialAggregationGroupedSummaryResponse
{
    public required Dictionary<string, object?> Key { get; init; }

    public string? Label { get; init; }

    public required Dictionary<string, SpatialAggregationSummaryValueResponse> Summaries { get; init; }
}

internal sealed class SpatialAggregationSummaryValueResponse
{
    public required string Kind { get; init; }

    public JsonElement? Value { get; init; }

    public string? Unit { get; init; }

    public bool? Approximate { get; init; }

    public SpatialAggregationBucketValueResponse[]? Buckets { get; init; }

    public long? OtherCount { get; init; }

    public long? NullCount { get; init; }
}

internal sealed class SpatialAggregationBucketValueResponse
{
    public object? Value { get; init; }

    public string? Label { get; init; }

    public long? Count { get; init; }

    public string? Color { get; init; }

    public string? Id { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool? IncludeMin { get; init; }

    public bool? IncludeMax { get; init; }
}

internal sealed class SpatialAggregationPageInfoResponse
{
    public int? LoadedCellCount { get; init; }

    public int? TotalCellCount { get; init; }
}

internal sealed class SpatialAggregationDegradedReasonResponse
{
    public required string Capability { get; init; }

    public required string Protocol { get; init; }

    public required string SourceId { get; init; }

    public required string Reason { get; init; }
}
