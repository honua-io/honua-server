// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// The response an <see cref="ICustomEndpoint"/> handler returns. The host writes the status code,
/// content type, headers, and body back to the caller. This is a value-typed, ASP.NET-free DTO so
/// the SDK contract stays dependency-minimal and AOT-safe.
/// </summary>
/// <param name="StatusCode">The HTTP status code to return.</param>
/// <param name="ContentType">The response content type (e.g. <c>application/json</c>).</param>
/// <param name="Body">The response body bytes.</param>
/// <param name="Headers">Additional response headers to emit.</param>
public sealed record PluginEndpointResponse(
    int StatusCode,
    string? ContentType,
    ReadOnlyMemory<byte> Body,
    ImmutableDictionary<string, string?> Headers)
{
    /// <summary>
    /// Creates a <c>200 OK</c> JSON response from a UTF-8 string payload.
    /// </summary>
    /// <param name="json">The JSON payload (UTF-8 encoded by this helper).</param>
    /// <returns>A JSON response.</returns>
    public static PluginEndpointResponse Json(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new PluginEndpointResponse(
            StatusCode: 200,
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes(json),
            Headers: ImmutableDictionary<string, string?>.Empty);
    }

    /// <summary>
    /// Creates a plain-text response with the given status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="text">The text payload (UTF-8 encoded by this helper).</param>
    /// <returns>A text response.</returns>
    public static PluginEndpointResponse Text(int statusCode, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PluginEndpointResponse(
            statusCode,
            ContentType: "text/plain; charset=utf-8",
            Body: Encoding.UTF8.GetBytes(text),
            Headers: ImmutableDictionary<string, string?>.Empty);
    }

    /// <summary>
    /// Creates an empty response with the given status code (no body).
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>An empty-bodied response.</returns>
    public static PluginEndpointResponse Status(int statusCode)
        => new(statusCode, ContentType: null, Body: ReadOnlyMemory<byte>.Empty, ImmutableDictionary<string, string?>.Empty);
}
