// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Domain;

// Collections stay ordered lists in authoring order so identical input serializes to the same bytes.
public sealed class TileStyleSpec
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = TileStyleSpecDefaults.Encoding;

    [JsonPropertyName("version")]
    public string Version { get; set; } = TileStyleSpecDefaults.Version;

    [JsonPropertyName("defaultMaterial")]
    public TileStyleMaterial DefaultMaterial { get; set; } = new();

    [JsonPropertyName("style")]
    public TileStyleExpressions Style { get; set; } = new();
}

public sealed class TileStyleMaterial
{
    [JsonPropertyName("color")]
    public string Color { get; set; } = Symbology3DColor.White.ToHex();

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;
}

public sealed class TileStyleExpressions
{
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileStyleConditions? Color { get; set; }

    [JsonPropertyName("show")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileStyleConditions? Show { get; set; }
}

public sealed class TileStyleConditions
{
    [JsonPropertyName("conditions")]
    public IReadOnlyList<string[]> Conditions { get; set; } = [];
}

public static class TileStyleSpecDefaults
{
    public const string Encoding = "3d-tiles-styling";

    public const string Version = "1.0";

    public const string FileName = "style.json";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TileStyleSpec))]
[JsonSerializable(typeof(TileStyleMaterial))]
[JsonSerializable(typeof(TileStyleExpressions))]
[JsonSerializable(typeof(TileStyleConditions))]
public sealed partial class TileStyleSpecJsonContext : JsonSerializerContext
{
}
