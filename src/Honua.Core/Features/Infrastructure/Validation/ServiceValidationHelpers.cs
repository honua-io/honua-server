// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Specialized validation helpers for common Honua service patterns.
/// Provides type-safe validation methods for the most frequent dependency patterns.
/// </summary>
public static class ServiceValidationHelpers
{
    /// <summary>
    /// Validates the most common service constructor pattern: IDatabaseConnectionProvider + ILogger.
    /// Used in over 80% of service constructors across the codebase.
    /// </summary>
    /// <typeparam name="T">The logger category type</typeparam>
    /// <param name="connectionProvider">Database connection provider</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (IDatabaseConnectionProvider, ILogger<T>) ValidateServiceDependencies<T>(
        [NotNull] IDatabaseConnectionProvider? connectionProvider,
        [NotNull] ILogger<T>? logger)
    {
        return (
            connectionProvider.ThrowIfNull(),
            logger.ThrowIfNull()
        );
    }

    /// <summary>
    /// Validates service constructor with connection provider, logger, and options.
    /// Second most common pattern in the codebase.
    /// </summary>
    /// <typeparam name="TService">The service type for logger category</typeparam>
    /// <typeparam name="TOptions">The options type</typeparam>
    /// <param name="connectionProvider">Database connection provider</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="options">Configuration options</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (IDatabaseConnectionProvider, ILogger<TService>, TOptions) ValidateServiceDependencies<TService, TOptions>(
        [NotNull] IDatabaseConnectionProvider? connectionProvider,
        [NotNull] ILogger<TService>? logger,
        [NotNull] IOptions<TOptions>? options)
        where TOptions : class
    {
        return (
            connectionProvider.ThrowIfNull(),
            logger.ThrowIfNull(),
            options.ValidateAndGetValue()
        );
    }

    /// <summary>
    /// Validates cache decorator pattern: inner service + cache service + options.
    /// Common pattern for caching decorators throughout the system.
    /// </summary>
    /// <typeparam name="TInner">The inner service interface type</typeparam>
    /// <typeparam name="TCacheService">The cache service type</typeparam>
    /// <typeparam name="TOptions">The cache options type</typeparam>
    /// <param name="innerService">The inner service being decorated</param>
    /// <param name="cacheService">The cache service</param>
    /// <param name="options">Cache configuration options</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (TInner, TCacheService, TOptions) ValidateCacheDecoratorDependencies<TInner, TCacheService, TOptions>(
        [NotNull] TInner? innerService,
        [NotNull] TCacheService? cacheService,
        [NotNull] IOptions<TOptions>? options)
        where TInner : class
        where TCacheService : class
        where TOptions : class
    {
        return (
            innerService.ThrowIfNull(),
            cacheService.ThrowIfNull(),
            options.ValidateAndGetValue()
        );
    }

    /// <summary>
    /// Validates handler dependencies pattern: multiple services without options.
    /// Common in OGC and API handlers.
    /// </summary>
    /// <typeparam name="T1">First dependency type</typeparam>
    /// <typeparam name="T2">Second dependency type</typeparam>
    /// <typeparam name="T3">Third dependency type</typeparam>
    /// <typeparam name="TLogger">Logger category type</typeparam>
    /// <param name="dependency1">First service dependency</param>
    /// <param name="dependency2">Second service dependency</param>
    /// <param name="dependency3">Third service dependency</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (T1, T2, T3, ILogger<TLogger>) ValidateHandlerDependencies<T1, T2, T3, TLogger>(
        [NotNull] T1? dependency1,
        [NotNull] T2? dependency2,
        [NotNull] T3? dependency3,
        [NotNull] ILogger<TLogger>? logger)
        where T1 : class
        where T2 : class
        where T3 : class
    {
        return (
            dependency1.ThrowIfNull(),
            dependency2.ThrowIfNull(),
            dependency3.ThrowIfNull(),
            logger.ThrowIfNull()
        );
    }

    /// <summary>
    /// Validates background service dependencies: service + logger + options.
    /// Common pattern for background services and hosted services.
    /// </summary>
    /// <typeparam name="TService">Primary service type</typeparam>
    /// <typeparam name="TLogger">Logger category type</typeparam>
    /// <typeparam name="TOptions">Options type</typeparam>
    /// <param name="service">Primary service dependency</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="options">Configuration options</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (TService, ILogger<TLogger>, TOptions) ValidateBackgroundServiceDependencies<TService, TLogger, TOptions>(
        [NotNull] TService? service,
        [NotNull] ILogger<TLogger>? logger,
        [NotNull] IOptions<TOptions>? options)
        where TService : class
        where TOptions : class
    {
        return (
            service.ThrowIfNull(),
            logger.ThrowIfNull(),
            options.ValidateAndGetValue()
        );
    }

    /// <summary>
    /// Validates repository pattern dependencies: connection provider + registry + logger.
    /// Common in data access layer services.
    /// </summary>
    /// <typeparam name="TRegistry">Registry service type</typeparam>
    /// <typeparam name="TLogger">Logger category type</typeparam>
    /// <param name="connectionProvider">Database connection provider</param>
    /// <param name="registry">Registry service (layer catalog, CRS registry, etc.)</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (IDatabaseConnectionProvider, TRegistry, ILogger<TLogger>) ValidateRepositoryDependencies<TRegistry, TLogger>(
        [NotNull] IDatabaseConnectionProvider? connectionProvider,
        [NotNull] TRegistry? registry,
        [NotNull] ILogger<TLogger>? logger)
        where TRegistry : class
    {
        return (
            connectionProvider.ThrowIfNull(),
            registry.ThrowIfNull(),
            logger.ThrowIfNull()
        );
    }

    /// <summary>
    /// Validates complex service dependencies with up to 6 parameters.
    /// For services with many injected dependencies.
    /// </summary>
    public static (T1, T2, T3, T4, T5, T6) ValidateComplexServiceDependencies<T1, T2, T3, T4, T5, T6>(
        [NotNull] T1? param1,
        [NotNull] T2? param2,
        [NotNull] T3? param3,
        [NotNull] T4? param4,
        [NotNull] T5? param5,
        [NotNull] T6? param6)
        where T1 : class
        where T2 : class
        where T3 : class
        where T4 : class
        where T5 : class
        where T6 : class
    {
        return (
            param1.ThrowIfNull(),
            param2.ThrowIfNull(),
            param3.ThrowIfNull(),
            param4.ThrowIfNull(),
            param5.ThrowIfNull(),
            param6.ThrowIfNull()
        );
    }

    /// <summary>
    /// Validates event publisher dependencies: publisher + logger.
    /// Common pattern for event handling services.
    /// </summary>
    /// <typeparam name="TPublisher">Event publisher type</typeparam>
    /// <typeparam name="TLogger">Logger category type</typeparam>
    /// <param name="publisher">Event publisher service</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Tuple of validated dependencies</returns>
    public static (TPublisher, ILogger<TLogger>) ValidateEventServiceDependencies<TPublisher, TLogger>(
        [NotNull] TPublisher? publisher,
        [NotNull] ILogger<TLogger>? logger)
        where TPublisher : class
    {
        return (
            publisher.ThrowIfNull(),
            logger.ThrowIfNull()
        );
    }
}