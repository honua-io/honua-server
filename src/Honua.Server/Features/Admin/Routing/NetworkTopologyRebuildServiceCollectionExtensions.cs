// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Registers the durable shadow-topology rebuild worker (#2718) and its self-healing
/// reconciler (#2720). Self-gates on the routing rebuild store and the shared execution-job
/// store both being registered, mirroring <c>ProvisionerServiceCollectionExtensions</c>: on
/// a non-Postgres data provider or a Redis-less profile, this is a no-op rather than an
/// unconditional worker that can never claim anything.
/// </summary>
internal static class NetworkTopologyRebuildServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="NetworkTopologyRebuildJobExecutor"/> worker, the
    /// <see cref="NetworkTopologyRebuildSubmissionService"/> submission surface, and the
    /// <see cref="NetworkTopologyRebuildReconciler"/> self-healing background service.
    /// </summary>
    public static IServiceCollection AddNetworkTopologyRebuildJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(INetworkTopologyRebuildStore)))
        {
            // Non-Postgres data provider: the rebuild store (and every other network-topology
            // store) is not registered, so there is nothing this worker could ever claim.
            return services;
        }

        services.AddSingleton<IJobExecutor, NetworkTopologyRebuildJobExecutor>();

        // NetworkTopologyRebuildSubmissionService depends on IServiceProvider (always
        // resolvable) rather than IExecutionJobStore directly, resolving the job store lazily
        // per-call and failing with a clean 503 when it is absent. It is always safe to
        // register: on a Redis-less profile the route stays mapped but returns 503 instead of
        // an app-wide ValidateOnBuild failure at startup (secondary-provider-dormant-startup).
        services.TryAddScoped<NetworkTopologyRebuildSubmissionService>();

        if (services.Any(d => d.ServiceType == typeof(IExecutionJobStore)))
        {
            // NetworkTopologyRebuildReconciler takes IExecutionJobStore directly (it needs the
            // durable job store to distinguish an orphaned attempt, owning job already
            // terminal, from one merely awaiting worker takeover), so it is only registered
            // once AddJobOrchestration has already provided that dependency — otherwise
            // ValidateOnBuild would fail every Redis-less startup for a service nothing calls.
            services.TryAddScoped<NetworkTopologyRebuildReconciler>();
            services.AddHostedService<NetworkTopologyRebuildReconcilerBackgroundService>();
        }

        return services;
    }
}
