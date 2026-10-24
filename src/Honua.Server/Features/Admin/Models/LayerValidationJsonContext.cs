// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for layer validation admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerValidationResponse))]
[JsonSerializable(typeof(LayerValidationCheck))]
[JsonSerializable(typeof(LayerValidationCheck[]))]
[JsonSerializable(typeof(ApiResponse<LayerValidationResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class LayerValidationJsonContext : JsonSerializerContext
{
}
