// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Rendering;

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
