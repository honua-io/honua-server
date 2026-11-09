// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Validation;

public static class ValidationExtensions
{
    [return: NotNull]
    public static T ThrowIfNull<T>([NotNull] this T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    public static T ThrowIfNull<T>(this T? value, [CallerArgumentExpression("value")] string? parameterName = null)
        where T : struct
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    [return: NotNull]
    public static T ValidateAndGetValue<T>([NotNull] this IOptions<T>? options, [CallerArgumentExpression("options")] string? parameterName = null)
        where T : class
    {
        if (options == null)
            throw new ArgumentNullException(parameterName);

        return options.Value ?? throw new ArgumentNullException($"{parameterName}.Value");
    }

    [return: NotNull]
    public static IEnumerable<T> ThrowIfNullOrEmpty<T>([NotNull] this IEnumerable<T>? collection, [CallerArgumentExpression("collection")] string? parameterName = null)
    {
        if (collection == null)
            throw new ArgumentNullException(parameterName);

        if (!collection.Any())
            throw new ArgumentException("Collection cannot be empty.", parameterName);

        return collection;
    }

    [return: NotNull]
    public static string ThrowIfNullOrEmpty([NotNull] this string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("String cannot be empty or whitespace.", parameterName);

        return value;
    }

    [return: NotNull]
    public static string ThrowIfNull([NotNull] this string? value, [CallerArgumentExpression("value")] string? parameterName = null)
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

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
