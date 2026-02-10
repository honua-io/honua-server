// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.GeometryService.Models;

/// <summary>
/// Source-generated JSON serializer context for geometry service types (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BufferRequest))]
[JsonSerializable(typeof(SimplifyRequest))]
[JsonSerializable(typeof(ProjectRequest))]
[JsonSerializable(typeof(GeometryServiceResponse))]
[JsonSerializable(typeof(GeometryServiceErrorResponse))]
internal sealed partial class GeometryServiceJsonContext : JsonSerializerContext;
