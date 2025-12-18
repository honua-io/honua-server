// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.HealthCheck;
using Honua.Postgres.HealthCheck;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Postgres;

/// <summary>
/// Dependency injection extensions for PostgreSQL services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL services including health checking
    /// </summary>
    public static IServiceCollection AddPostgreSqlServices(this IServiceCollection services)
    {
        services.AddScoped<IDatabaseHealthChecker, PostgresDatabaseHealthChecker>();

        return services;
    }
}
