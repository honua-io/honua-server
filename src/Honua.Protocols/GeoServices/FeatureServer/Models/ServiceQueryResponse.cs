// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class ServiceQueryResponse
{
    [JsonPropertyName("layers")]
    public ServiceQueryLayerResponse[] Layers { get; init; } = [];
}

public sealed class ServiceQueryLayerResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    public string? GeometryType { get; init; }

    public GeoServicesSpatialReference? SpatialReference { get; init; }

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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExceededTransferLimit { get; init; }
}
