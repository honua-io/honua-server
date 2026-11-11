// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

internal sealed record LayerDisplayUpdateRequest
{
    public double? MinScale { get; init; }
    public double? MaxScale { get; init; }
    public bool? DefaultVisibility { get; init; }
    public string? DisplayField { get; init; }
    public bool? Queryable { get; init; }
    public bool? HasZ { get; init; }
    public bool? HasM { get; init; }
}

internal sealed record LayerDisplayResponse
{
    public int LayerId { get; init; }
    public double? MinScale { get; init; }
    public double? MaxScale { get; init; }
    public bool DefaultVisibility { get; init; }
    public string? DisplayField { get; init; }
    public bool Queryable { get; init; }
    public bool HasZ { get; init; }
    public bool HasM { get; init; }
}

internal sealed record LayerSpatialUpdateRequest
{
    public MetadataV2SpatialReference? SpatialReference { get; init; }
    public MetadataV2GeometryType? GeometryType { get; init; }
    public IReadOnlyList<MetadataV2SpatialReference>? SupportedCrs { get; init; }
    public MetadataV2SpatialReference? StorageCrs { get; init; }
    public bool ClearStorageCrs { get; init; }
    public double? StorageCrsCoordinateEpoch { get; init; }
    public bool ClearStorageCrsCoordinateEpoch { get; init; }
}

internal sealed record LayerSpatialResponse
{
    public int LayerId { get; init; }
    public MetadataV2SpatialReference? SpatialReference { get; init; }
    public MetadataV2GeometryType GeometryType { get; init; }
    public IReadOnlyList<MetadataV2SpatialReference> SupportedCrs { get; init; } = [];
    public MetadataV2SpatialReference? StorageCrs { get; init; }
    public double? StorageCrsCoordinateEpoch { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LayerDisplayUpdateRequest))]
[JsonSerializable(typeof(LayerSpatialUpdateRequest))]
[JsonSerializable(typeof(ApiResponse<LayerDisplayResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerSpatialResponse>))]
internal sealed partial class LayerDisplaySpatialJsonContext : JsonSerializerContext;
