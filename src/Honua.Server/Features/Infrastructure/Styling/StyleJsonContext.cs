// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// JSON serialization context for style payloads.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, object?>), TypeInfoPropertyName = "DictionaryStringObject")]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<Dictionary<string, object?>>))]
[JsonSerializable(typeof(List<object?>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class StyleJsonContext : JsonSerializerContext
{
}
