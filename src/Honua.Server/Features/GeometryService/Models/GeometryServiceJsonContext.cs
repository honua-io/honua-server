// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.GeometryService.Models;

/// <summary>
/// Source-generated JSON serializer context for geometry service types (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeometryServiceResponse))]
[JsonSerializable(typeof(GeometryServiceSpatialReference))]
[JsonSerializable(typeof(GeometryServiceAreasAndLengthsResponse))]
[JsonSerializable(typeof(GeometryServiceLengthResponse))]
[JsonSerializable(typeof(GeometryServiceErrorResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GeometryServiceJsonContext : JsonSerializerContext;
