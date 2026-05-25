// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.TemporalHistory;

/// <summary>
/// Registration helpers for the temporal data-history feature. The concrete
/// <c>ITemporalHistorySource</c> is provided by the active data provider (for example
/// <c>Honua.Postgres</c>); here we register the job-runner executor that applies rollbacks.
/// </summary>
internal static class TemporalHistoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the temporal-history job executor. Safe to call in API-only hosts where the worker
    /// loop is absent — the executor is only consumed when the job worker is running.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTemporalHistory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IJobExecutor, TemporalRollbackJobExecutor>();
        return services;
    }
}
