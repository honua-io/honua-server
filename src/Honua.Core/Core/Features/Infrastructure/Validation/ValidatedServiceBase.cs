// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Validation;

public abstract class ValidatedServiceBase
{
    [return: NotNull]
    public static T ValidateRequired<T>([NotNull] T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    public static T ValidateRequired<T>(T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : struct
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    [return: NotNull]
    public static T ValidateOptions<T>([NotNull] IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
        where T : class
    {
        if (options == null)
            throw new ArgumentNullException(parameterName);

        return options.Value ?? throw new ArgumentNullException($"{parameterName}.Value");
    }

    [return: NotNull]
    public static IEnumerable<T> ValidateCollectionNotEmpty<T>([NotNull] IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
    {
        if (collection == null)
            throw new ArgumentNullException(parameterName);

        if (!collection.Any())
            throw new ArgumentException("Collection cannot be empty.", parameterName);

        return collection;
    }

    [return: NotNull]
    public static string ValidateNotEmpty([NotNull] string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("String cannot be empty or whitespace.", parameterName);

        return value;
    }

    protected static ValidationBuilder Validate() => new();
}

public sealed class ValidationBuilder
{
    public ValidationBuilder Required<T>([NotNull] T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        ValidatedServiceBase.ValidateRequired(value, parameterName);
        return this;
    }

    public ValidationBuilder Required<T>(T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : struct
    {
        ValidatedServiceBase.ValidateRequired(value, parameterName);
        return this;
    }

    public ValidationBuilder Options<T>([NotNull] IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
        where T : class
    {
        ValidatedServiceBase.ValidateOptions(options, parameterName);
        return this;
    }

    public ValidationBuilder CollectionNotEmpty<T>([NotNull] IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
    {
        ValidatedServiceBase.ValidateCollectionNotEmpty(collection, parameterName);
        return this;
    }

    public ValidationBuilder NotEmpty([NotNull] string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        ValidatedServiceBase.ValidateNotEmpty(value, parameterName);
        return this;
    }

    public ValidationBuilder That(bool condition, Exception exception)
    {
        if (!condition)
            throw exception;
        return this;
    }

    public ValidationBuilder That(bool condition, string message, string? parameterName = null)
    {
        if (!condition)
            throw new ArgumentException(message, parameterName);
        return this;
    }
}
