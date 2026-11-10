// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class LayerInfo
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public int? ParentLayerId { get; init; }

    public bool DefaultVisibility { get; init; } = true;

    public int[]? SubLayerIds { get; init; }

    public double? MinScale { get; init; }

    public double? MaxScale { get; init; }

    public string Type { get; init; } = "Feature Layer";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? GeometryType { get; init; }
}
