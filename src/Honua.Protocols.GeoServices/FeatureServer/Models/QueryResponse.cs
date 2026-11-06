// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class QueryResponse : ICollectionResponse<GeoServicesFeature>
{
    public string? GeometryType { get; init; }

    public GeoServicesSpatialReference? SpatialReference { get; init; }

    // Transform is declared ahead of Features so a client reading the featureSet front to back dequantizes each geometry with it; a transform after the features draws nothing.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesTransform? Transform { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayFieldName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFieldInfo[]? Fields { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectIdFieldName { get; init; }

    public long[]? ObjectIds { get; init; }

    public long? Count { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtentInfo? Extent { get; init; }

    public string? UniqueIdField { get; init; }

    public string? GlobalIdFieldName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFeature[]? Features { get; init; }

    // exceededTransferLimit is always emitted, including false, because the ArcGIS API for Python query_top_features paginator throws when the field is missing.
    public bool ExceededTransferLimit { get; init; }

    ImmutableArray<GeoServicesFeature> ICollectionResponse<GeoServicesFeature>.Items =>
        Features?.ToImmutableArray() ?? ImmutableArray<GeoServicesFeature>.Empty;
    ImmutableArray<ILink>? ICollectionResponse<GeoServicesFeature>.Links => null; // FeatureServer doesn't use links
    IPaginationMetadata? ICollectionResponse<GeoServicesFeature>.Pagination =>
        Count.HasValue || (Features?.Length ?? 0) > 0
            ? PaginationMetadata.Create(Count, Features?.Length ?? 0, ExceededTransferLimit)
            : null;
}
