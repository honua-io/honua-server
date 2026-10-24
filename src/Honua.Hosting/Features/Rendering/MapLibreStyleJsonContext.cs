// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// AOT-compatible JSON serialization context for MapLibre style parsing.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    Converters = new[] { typeof(MapLibreExpressionConverter) })]
[JsonSerializable(typeof(MapLibreStyleDocument))]
[JsonSerializable(typeof(MapLibreStyleLayer))]
[JsonSerializable(typeof(MapLibreStyleLayer[]))]
[JsonSerializable(typeof(Dictionary<string, MapLibreExpression>))]
[JsonSerializable(typeof(MapLibreExpression))]
[JsonSerializable(typeof(MapLibreExpression[]))]
internal sealed partial class MapLibreStyleJsonContext : JsonSerializerContext;
