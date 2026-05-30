// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Styling.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for layer style admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerStyleUpdateRequest))]
[JsonSerializable(typeof(LayerStyleResponse))]
[JsonSerializable(typeof(ApiResponse<LayerStyleResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(UnsupportedSymbolizerInfo))]
[JsonSerializable(typeof(IReadOnlyList<UnsupportedSymbolizerInfo>))]
public sealed partial class LayerStyleJsonContext : JsonSerializerContext
{
}
