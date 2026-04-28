// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Generic API response wrapper for successful responses.
/// </summary>
/// <typeparam name="T">Type of the response data</typeparam>
public sealed class ApiResponse<T>
{
    /// <summary>
    /// Whether the request was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    /// <summary>
    /// The response data payload.
    /// </summary>
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    /// <summary>
    /// Optional message about the response.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Timestamp when the response was generated.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates a successful response with data.
    /// </summary>
    /// <param name="data">The response data</param>
    /// <param name="message">Optional success message</param>
    /// <returns>Successful API response</returns>
    public static ApiResponse<T> CreateSuccess(T data, string? message = null)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Message = message
        };
    }

    /// <summary>
    /// Creates a successful response without data.
    /// </summary>
    /// <param name="message">Optional success message</param>
    /// <returns>Successful API response</returns>
    public static ApiResponse<T> SuccessWithMessage(string? message = null)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Message = message
        };
    }

    /// <summary>
    /// Creates a failure response.
    /// </summary>
    /// <param name="message">Error message</param>
    /// <returns>Failed API response</returns>
    public static ApiResponse<T> Failure(string message)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Message = message
        };
    }

    /// <summary>
    /// Creates a failure response with a structured payload.
    /// </summary>
    /// <param name="message">Error message</param>
    /// <param name="data">Failure response data</param>
    /// <returns>Failed API response</returns>
    public static ApiResponse<T> Failure(string message, T? data)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Data = data,
            Message = message
        };
    }
}
