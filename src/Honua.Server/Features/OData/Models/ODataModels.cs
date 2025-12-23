// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// OData service document response
/// </summary>
public sealed class ServiceDocument
{
    /// <summary>
    /// OData context URL
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public required string Context { get; init; }

    /// <summary>
    /// Available entity sets
    /// </summary>
    [JsonPropertyName("value")]
    public required EntitySet[] Value { get; init; }
}

/// <summary>
/// OData entity set definition
/// </summary>
public sealed class EntitySet
{
    /// <summary>
    /// Entity set name
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Entity set URL
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>
/// Generic OData collection response
/// </summary>
public sealed class ODataResponse
{
    /// <summary>
    /// OData context URL
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public required string Context { get; init; }

    /// <summary>
    /// Total count of items (when $count=true)
    /// </summary>
    [JsonPropertyName("@odata.count")]
    public long? Count { get; init; }

    /// <summary>
    /// Next link for pagination
    /// </summary>
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }

    /// <summary>
    /// Collection of data items
    /// </summary>
    [JsonPropertyName("value")]
    public required object[] Value { get; init; }
}

/// <summary>
/// OData error response
/// </summary>
public sealed class ODataError
{
    /// <summary>
    /// Error details
    /// </summary>
    [JsonPropertyName("error")]
    public required ErrorDetails Error { get; init; }
}

/// <summary>
/// OData error details
/// </summary>
public sealed class ErrorDetails
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Additional error details
    /// </summary>
    [JsonPropertyName("details")]
    public ErrorDetail[]? Details { get; init; }
}

/// <summary>
/// Individual error detail
/// </summary>
public sealed class ErrorDetail
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Error message
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Error target
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }
}
