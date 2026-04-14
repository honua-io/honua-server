// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Extension methods for common parameter validation patterns.
/// Provides fluent validation syntax for any class, not just ValidatedServiceBase derived classes.
/// </summary>
public static class ValidationExtensions
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
    public static T ThrowIfNull<T>([NotNull] this T? value, [CallerArgumentExpression("value")] string? parameterName = null)
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
    public static T ThrowIfNull<T>(this T? value, [CallerArgumentExpression("value")] string? parameterName = null)
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
    public static T ValidateAndGetValue<T>([NotNull] this IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
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
    public static IEnumerable<T> ThrowIfNullOrEmpty<T>([NotNull] this IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
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
    public static string ThrowIfNullOrEmpty([NotNull] this string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("String cannot be empty or whitespace.", parameterName);

        return value;
    }

    /// <summary>
    /// Validates that a string parameter is not null.
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="parameterName">The parameter name (automatically inferred)</param>
    /// <returns>The validated non-null string (may be empty)</returns>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    [return: NotNull]
    public static string ThrowIfNull([NotNull] this string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    /// <summary>
    /// Validates multiple constructor parameters with common patterns.
    /// Optimized for the most frequent dependency patterns in the codebase.
    /// </summary>
    /// <typeparam name="T1">First dependency type</typeparam>
    /// <typeparam name="T2">Second dependency type</typeparam>
    /// <param name="param1">First parameter to validate</param>
    /// <param name="param2">Second parameter to validate</param>
    /// <param name="param1Name">First parameter name (automatically inferred)</param>
    /// <param name="param2Name">Second parameter name (automatically inferred)</param>
    /// <returns>Tuple of validated parameters</returns>
    public static (T1, T2) ValidateConstructorParameters<T1, T2>(
        [NotNull] T1? param1,
        [NotNull] T2? param2,
        [CallerArgumentExpression("param1")] string? param1Name = null,
        [CallerArgumentExpression("param2")] string? param2Name = null)
        where T1 : class
        where T2 : class
    {
        return (
            param1.ThrowIfNull(param1Name),
            param2.ThrowIfNull(param2Name)
        );
    }

    /// <summary>
    /// Validates three constructor parameters with common patterns.
    /// </summary>
    public static (T1, T2, T3) ValidateConstructorParameters<T1, T2, T3>(
        [NotNull] T1? param1,
        [NotNull] T2? param2,
        [NotNull] T3? param3,
        [CallerArgumentExpression("param1")] string? param1Name = null,
        [CallerArgumentExpression("param2")] string? param2Name = null,
        [CallerArgumentExpression("param3")] string? param3Name = null)
        where T1 : class
        where T2 : class
        where T3 : class
    {
        return (
            param1.ThrowIfNull(param1Name),
            param2.ThrowIfNull(param2Name),
            param3.ThrowIfNull(param3Name)
        );
    }

    /// <summary>
    /// Validates four constructor parameters with common patterns.
    /// </summary>
    public static (T1, T2, T3, T4) ValidateConstructorParameters<T1, T2, T3, T4>(
        [NotNull] T1? param1,
        [NotNull] T2? param2,
        [NotNull] T3? param3,
        [NotNull] T4? param4,
        [CallerArgumentExpression("param1")] string? param1Name = null,
        [CallerArgumentExpression("param2")] string? param2Name = null,
        [CallerArgumentExpression("param3")] string? param3Name = null,
        [CallerArgumentExpression("param4")] string? param4Name = null)
        where T1 : class
        where T2 : class
        where T3 : class
        where T4 : class
    {
        return (
            param1.ThrowIfNull(param1Name),
            param2.ThrowIfNull(param2Name),
            param3.ThrowIfNull(param3Name),
            param4.ThrowIfNull(param4Name)
        );
    }

    /// <summary>
    /// Validates five constructor parameters with common patterns.
    /// </summary>
    public static (T1, T2, T3, T4, T5) ValidateConstructorParameters<T1, T2, T3, T4, T5>(
        [NotNull] T1? param1,
        [NotNull] T2? param2,
        [NotNull] T3? param3,
        [NotNull] T4? param4,
        [NotNull] T5? param5,
        [CallerArgumentExpression("param1")] string? param1Name = null,
        [CallerArgumentExpression("param2")] string? param2Name = null,
        [CallerArgumentExpression("param3")] string? param3Name = null,
        [CallerArgumentExpression("param4")] string? param4Name = null,
        [CallerArgumentExpression("param5")] string? param5Name = null)
        where T1 : class
        where T2 : class
        where T3 : class
        where T4 : class
        where T5 : class
    {
        return (
            param1.ThrowIfNull(param1Name),
            param2.ThrowIfNull(param2Name),
            param3.ThrowIfNull(param3Name),
            param4.ThrowIfNull(param4Name),
            param5.ThrowIfNull(param5Name)
        );
    }

    /// <summary>
    /// Validates the most common constructor pattern: ConnectionProvider + Logger + Options.
    /// </summary>
    /// <typeparam name="TConnectionProvider">Connection provider type</typeparam>
    /// <typeparam name="TLogger">Logger type</typeparam>
    /// <typeparam name="TOptions">Options type</typeparam>
    /// <param name="connectionProvider">Database connection provider</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="options">Configuration options</param>
    /// <returns>Tuple of validated parameters</returns>
    public static (TConnectionProvider, TLogger, TOptions) ValidateCommonDependencies<TConnectionProvider, TLogger, TOptions>(
        [NotNull] TConnectionProvider? connectionProvider,
        [NotNull] TLogger? logger,
        [NotNull] IOptions<TOptions>? options)
        where TConnectionProvider : class
        where TLogger : class
        where TOptions : class
    {
        return (
            connectionProvider.ThrowIfNull(),
            logger.ThrowIfNull(),
            options.ValidateAndGetValue()
        );
    }
}
