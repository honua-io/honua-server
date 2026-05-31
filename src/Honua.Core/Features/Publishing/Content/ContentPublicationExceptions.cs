// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Contracts;

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

/// <summary>
/// A client request failed validation (HTTP 400). Carries one or more
/// field-addressable <see cref="FieldValidationError"/>s so endpoints can return an
/// RFC-7807 ProblemDetails with an <c>errors[]</c> extension that the console binds
/// onto the offending report/dashboard inputs. The exception <see cref="System.Exception.Message"/>
/// remains the first error's message for log/back-compat surfaces.
/// </summary>
public sealed class ContentPublicationValidationException : ContentPublicationException
{
    /// <summary>
    /// Creates a single-field validation error. The <paramref name="code"/> and
    /// <paramref name="path"/> make the failure field-addressable; both default to
    /// safe values so legacy call sites that pass only a message still produce a
    /// well-formed (form-level) <see cref="FieldValidationError"/>.
    /// </summary>
    public ContentPublicationValidationException(
        string message,
        string code = "publication.invalid",
        string? path = null)
        : base(400, message)
    {
        Errors =
        [
            FieldValidationError.Create(code, message, ValidationSeverity.Error, path),
        ];
    }

    /// <summary>
    /// Creates a validation error carrying a pre-built set of field-addressable
    /// errors (for body validation that collects multiple findings in one pass).
    /// The exception message is the first error's message.
    /// </summary>
    public ContentPublicationValidationException(IReadOnlyList<FieldValidationError> errors)
        : base(400, ResolveMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("At least one field validation error is required.", nameof(errors));
        }

        Errors = errors;
    }

    /// <summary>The field-addressable validation errors carried by this rejection (never empty).</summary>
    public IReadOnlyList<FieldValidationError> Errors { get; }

    private static string ResolveMessage(IReadOnlyList<FieldValidationError> errors)
        => errors is { Count: > 0 } ? errors[0].Message : "The content publication request failed validation.";
}

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
