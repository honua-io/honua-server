// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Http;

/// <summary>
/// Abstraction for HTTP request context information needed by Core project components.
/// This allows the Core project to remain independent of ASP.NET Core types.
/// </summary>
public interface IRequestContext
{
    /// <summary>
    /// Base URL for the current request.
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Full request URL.
    /// </summary>
    string RequestUrl { get; }

    /// <summary>
    /// Request scheme (http/https).
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Host name from the request.
    /// </summary>
    string Host { get; }

    /// <summary>
    /// Request path.
    /// </summary>
    string Path { get; }

    /// <summary>
    /// Query string parameters.
    /// </summary>
    IReadOnlyDictionary<string, string> QueryParameters { get; }

    /// <summary>
    /// Request headers.
    /// </summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// User agent string.
    /// </summary>
    string? UserAgent { get; }

    /// <summary>
    /// Client IP address.
    /// </summary>
    string? ClientIP { get; }

    /// <summary>
    /// Gets the value of a specific query parameter.
    /// </summary>
    /// <param name="name">Parameter name</param>
    /// <returns>Parameter value or null if not found</returns>
    string? GetQueryParameter(string name);

    /// <summary>
    /// Gets the value of a specific header.
    /// </summary>
    /// <param name="name">Header name</param>
    /// <returns>Header value or null if not found</returns>
    string? GetHeader(string name);
}