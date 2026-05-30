// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Models;

/// <summary>
/// Represents a GeoServices-compatible API error response
/// Follows GeoServices REST error response format: { "error": { "code": number, "message": "string", "details": [] } }
/// </summary>
public sealed class ApiErrorResponse
{
    /// <summary>
    /// The error details
    /// </summary>
    [JsonPropertyName("error")]
    public required GeoServicesError Error { get; init; }
}

/// <summary>
/// Represents the error details in GeoServices-compatible format
/// </summary>
public sealed class GeoServicesError
{
    /// <summary>
    /// Error code number
    /// </summary>
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    /// <summary>
    /// Error message text
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Optional array of additional error details
    /// </summary>
    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}
