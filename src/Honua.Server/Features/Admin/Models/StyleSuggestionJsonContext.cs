// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for style suggestion admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(StyleSuggestionRequest))]
[JsonSerializable(typeof(StyleSuggestionResponse))]
[JsonSerializable(typeof(StyleSuggestionLegend))]
[JsonSerializable(typeof(StyleSuggestionLegendEntry))]
[JsonSerializable(typeof(StyleSuggestionLegendEntry[]))]
[JsonSerializable(typeof(StyleSuggestionFieldInfo))]
[JsonSerializable(typeof(ApiResponse<StyleSuggestionResponse>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string[]))]
public sealed partial class StyleSuggestionJsonContext : JsonSerializerContext
{
}
