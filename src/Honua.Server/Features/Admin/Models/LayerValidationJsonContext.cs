// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

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
