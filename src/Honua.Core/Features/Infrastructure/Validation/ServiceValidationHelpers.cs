// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Validation;

public static class ServiceValidationHelpers
{
    public static (IDatabaseConnectionProvider, ILogger<T>) ValidateServiceDependencies<T>(
        [NotNull] IDatabaseConnectionProvider? connectionProvider,
        [NotNull] ILogger<T>? logger)
    {
        return (
            connectionProvider.ThrowIfNull(),
            logger.ThrowIfNull()
        );
    }

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
