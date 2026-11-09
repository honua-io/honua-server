// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoJsonFeatureSet
{
    public string Type { get; init; } = "FeatureCollection";

    public GeoJsonFeature[] Features { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExceededTransferLimit { get; init; }

    public Dictionary<string, object?>? Properties { get; init; }
}

public sealed class GeoJsonFeature
{
    public string Type { get; init; } = "Feature";

    public required Dictionary<string, object?> Properties { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public GeoJsonGeometry? Geometry { get; init; }

    public object? Id { get; init; }
}

public sealed class GeoJsonGeometry
{
    public required string Type { get; init; }

    public required object? Coordinates { get; init; }

    public GeoJsonGeometry[]? Geometries { get; init; }
}
