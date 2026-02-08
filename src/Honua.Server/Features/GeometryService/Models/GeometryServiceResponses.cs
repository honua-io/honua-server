// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.GeometryService.Models;

/// <summary>
/// Standard response for geometry service operations.
/// </summary>
public sealed class GeometryServiceResponse
{
    /// <summary>
    /// Array of result geometries in GeoServices JSON format.
    /// </summary>
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }
}

/// <summary>
/// Error response for geometry service operations.
/// </summary>
public sealed class GeometryServiceErrorResponse
{
    /// <summary>
    /// Error details.
    /// </summary>
    [JsonPropertyName("error")]
    public required GeometryServiceError Error { get; init; }
}

/// <summary>
/// Error details for geometry service operations.
/// </summary>
public sealed class GeometryServiceError
{
    /// <summary>
    /// Numeric error code.
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Additional error details.
    /// </summary>
    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}
