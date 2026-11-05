// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Domain;

public sealed class TilesetDocument
{
    [JsonPropertyName("asset")]
    public TilesetAsset Asset { get; set; } = new();

    [JsonPropertyName("geometricError")]
    public double GeometricError { get; set; }

    [JsonPropertyName("root")]
    public TileNode Root { get; set; } = new();

    [JsonPropertyName("extras")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TilesetExtras? Extras { get; set; }
}

public sealed class TilesetExtras
{
    [JsonPropertyName("honua_style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TilesetStyleReference? Style { get; set; }
}

public sealed class TilesetStyleReference
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

public sealed class TilesetAsset
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.1";

    [JsonPropertyName("generator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Generator { get; set; }
}

public sealed class TileNode
{
    [JsonPropertyName("boundingVolume")]
    public BoundingVolume BoundingVolume { get; set; } = new();

    [JsonPropertyName("geometricError")]
    public double GeometricError { get; set; }

    [JsonPropertyName("refine")]
    public string Refine { get; set; } = "ADD";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileContent? Content { get; set; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TileNode>? Children { get; set; }
}

public sealed class BoundingVolume
{
    [JsonPropertyName("region")]
    public double[] Region { get; set; } = new double[6];
}

public sealed class TileContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TilesetDocument))]
[JsonSerializable(typeof(TileNode))]
[JsonSerializable(typeof(BoundingVolume))]
[JsonSerializable(typeof(TileContent))]
[JsonSerializable(typeof(TilesetAsset))]
[JsonSerializable(typeof(TilesetExtras))]
[JsonSerializable(typeof(TilesetStyleReference))]
public sealed partial class TilesetJsonContext : JsonSerializerContext
{
}
