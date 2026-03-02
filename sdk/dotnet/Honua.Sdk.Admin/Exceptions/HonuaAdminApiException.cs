// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Sdk.Admin.Exceptions;

/// <summary>
/// Exception thrown when the Honua Admin API returns an error HTTP status code.
/// </summary>
public sealed class HonuaAdminApiException : Exception
{
    /// <summary>
    /// HTTP status code returned by the server.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The raw response body, if available.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HonuaAdminApiException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code returned by the server.</param>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    public HonuaAdminApiException(HttpStatusCode statusCode, string message, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HonuaAdminApiException"/> class with an inner exception.
    /// </summary>
    /// <param name="statusCode">The HTTP status code returned by the server.</param>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    /// <param name="innerException">The inner exception that caused this exception.</param>
    public HonuaAdminApiException(HttpStatusCode statusCode, string message, string? responseBody, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
