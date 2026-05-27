// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Content;

/// <summary>
/// Base for content-publication errors that carry a stable HTTP status code.
/// Messages are intended to be client-safe; callers must not append raw exception
/// internals, SQL, connection strings, or filesystem paths.
/// </summary>
public abstract class ContentPublicationException : Exception
{
    /// <summary>Initializes a new instance with a status code and message.</summary>
    protected ContentPublicationException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The stable HTTP status code that this error maps to.</summary>
    public int StatusCode { get; }
}

/// <summary>A client request failed validation (HTTP 400).</summary>
public sealed class ContentPublicationValidationException(string message)
    : ContentPublicationException(400, message);

/// <summary>The referenced publication does not exist (HTTP 404).</summary>
public sealed class ContentPublicationNotFoundException(string message)
    : ContentPublicationException(404, message);

/// <summary>A request conflicts with current state (HTTP 409).</summary>
public sealed class ContentPublicationConflictException(string message)
    : ContentPublicationException(409, message);

/// <summary>
/// A dependency reference could not be validated. Defaults to HTTP 409 (the
/// dependency is missing) but may be 503 when a required store is not registered.
/// </summary>
public sealed class ContentPublicationDependencyException(string message, int statusCode = 409)
    : ContentPublicationException(statusCode, message);
