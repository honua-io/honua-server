// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation.Contracts;

namespace Honua.Core.Features.Publishing.Content;

// Messages are client-safe and must not be extended with exception internals, SQL, connection strings, or filesystem paths.
public abstract class ContentPublicationException : Exception
{
    protected ContentPublicationException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class ContentPublicationValidationException : ContentPublicationException
{
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

    public IReadOnlyList<FieldValidationError> Errors { get; }

    private static string ResolveMessage(IReadOnlyList<FieldValidationError> errors)
        => errors is { Count: > 0 } ? errors[0].Message : "The content publication request failed validation.";
}

public sealed class ContentPublicationNotFoundException(string message)
    : ContentPublicationException(404, message);

public sealed class ContentPublicationConflictException(string message)
    : ContentPublicationException(409, message);

public sealed class ContentPublicationDependencyException(string message, int statusCode = 409)
    : ContentPublicationException(statusCode, message);
