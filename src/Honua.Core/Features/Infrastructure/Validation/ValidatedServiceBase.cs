// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Base class for services requiring constructor parameter validation.
/// Provides standardized validation methods to eliminate code duplication.
/// </summary>
public abstract class ValidatedServiceBase
{
    /// <summary>
    /// Validates that a required parameter is not null.
    /// </summary>
    /// <typeparam name="T">The parameter type</typeparam>
    /// <param name="value">The parameter value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated non-null value</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    [return: NotNull]
    public static T ValidateRequired<T>([NotNull] T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    /// <summary>
    /// Validates that a required value type parameter is not null when declared as nullable.
    /// </summary>
    /// <typeparam name="T">The value type</typeparam>
    /// <param name="value">The parameter value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated non-null value</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    public static T ValidateRequired<T>(T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : struct
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    /// <summary>
    /// Validates an IOptions parameter and extracts the Value.
    /// </summary>
    /// <typeparam name="T">The options type</typeparam>
    /// <param name="options">The IOptions instance to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated options value</returns>
    /// <exception cref="ArgumentNullException">Thrown when options or options.Value is null</exception>
    [return: NotNull]
    public static T ValidateOptions<T>([NotNull] IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
        where T : class
    {
        if (options == null)
            throw new ArgumentNullException(parameterName);

        return options.Value ?? throw new ArgumentNullException($"{parameterName}.Value");
    }

    /// <summary>
    /// Validates that a collection parameter is not null and not empty.
    /// </summary>
    /// <typeparam name="T">The collection element type</typeparam>
    /// <param name="collection">The collection to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated non-empty collection</returns>
    /// <exception cref="ArgumentNullException">Thrown when the collection is null</exception>
    /// <exception cref="ArgumentException">Thrown when the collection is empty</exception>
    [return: NotNull]
    public static IEnumerable<T> ValidateCollectionNotEmpty<T>([NotNull] IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
    {
        if (collection == null)
            throw new ArgumentNullException(parameterName);

        if (!collection.Any())
            throw new ArgumentException("Collection cannot be empty.", parameterName);

        return collection;
    }

    /// <summary>
    /// Validates that a string parameter is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated non-empty string</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    /// <exception cref="ArgumentException">Thrown when the value is empty or whitespace</exception>
    [return: NotNull]
    public static string ValidateNotEmpty([NotNull] string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("String cannot be empty or whitespace.", parameterName);

        return value;
    }

    /// <summary>
    /// Validates multiple parameters at once with a fluent API.
    /// </summary>
    /// <returns>A validation builder for chaining validations</returns>
    protected static ValidationBuilder Validate() => new();
}

/// <summary>
/// Fluent builder for validating multiple parameters.
/// </summary>
public sealed class ValidationBuilder
{
    /// <summary>
    /// Validates that a required parameter is not null.
    /// </summary>
    /// <typeparam name="T">The parameter type</typeparam>
    /// <param name="value">The parameter value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    public ValidationBuilder Required<T>([NotNull] T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        ValidatedServiceBase.ValidateRequired(value, parameterName);
        return this;
    }

    /// <summary>
    /// Validates that a required value type parameter is not null when declared as nullable.
    /// </summary>
    /// <typeparam name="T">The value type</typeparam>
    /// <param name="value">The parameter value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    public ValidationBuilder Required<T>(T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : struct
    {
        ValidatedServiceBase.ValidateRequired(value, parameterName);
        return this;
    }

    /// <summary>
    /// Validates an IOptions parameter.
    /// </summary>
    /// <typeparam name="T">The options type</typeparam>
    /// <param name="options">The IOptions instance to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when options or options.Value is null</exception>
    public ValidationBuilder Options<T>([NotNull] IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
        where T : class
    {
        ValidatedServiceBase.ValidateOptions(options, parameterName);
        return this;
    }

    /// <summary>
    /// Validates that a collection parameter is not null and not empty.
    /// </summary>
    /// <typeparam name="T">The collection element type</typeparam>
    /// <param name="collection">The collection to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when the collection is null</exception>
    /// <exception cref="ArgumentException">Thrown when the collection is empty</exception>
    public ValidationBuilder CollectionNotEmpty<T>([NotNull] IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
    {
        ValidatedServiceBase.ValidateCollectionNotEmpty(collection, parameterName);
        return this;
    }

    /// <summary>
    /// Validates that a string parameter is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    /// <exception cref="ArgumentException">Thrown when the value is empty or whitespace</exception>
    public ValidationBuilder NotEmpty([NotNull] string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        ValidatedServiceBase.ValidateNotEmpty(value, parameterName);
        return this;
    }

    /// <summary>
    /// Validates a custom condition with a specific exception.
    /// </summary>
    /// <param name="condition">The condition that must be true</param>
    /// <param name="exception">The exception to throw if the condition is false</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="Exception">The provided exception if the condition is false</exception>
    public ValidationBuilder That(bool condition, Exception exception)
    {
        if (!condition)
            throw exception;
        return this;
    }

    /// <summary>
    /// Validates a custom condition with an ArgumentException.
    /// </summary>
    /// <param name="condition">The condition that must be true</param>
    /// <param name="message">The error message</param>
    /// <param name="parameterName">The parameter name</param>
    /// <returns>This builder for method chaining</returns>
    /// <exception cref="ArgumentException">Thrown if the condition is false</exception>
    public ValidationBuilder That(bool condition, string message, string? parameterName = null)
    {
        if (!condition)
            throw new ArgumentException(message, parameterName);
        return this;
    }
}
