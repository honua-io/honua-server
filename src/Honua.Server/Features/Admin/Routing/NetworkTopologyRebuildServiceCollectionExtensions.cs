// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Routing;

// Registration is a no-op when the rebuild store or the job store is absent, so a host that cannot claim work does not start the worker.
internal static class NetworkTopologyRebuildServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkTopologyRebuildJobs(this IServiceCollection services)
        => services.AddNetworkTopologyRebuildJobs(configuration: null);

    public static IServiceCollection AddNetworkTopologyRebuildJobs(
        this IServiceCollection services,
        IConfiguration? configuration)
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
            if (configuration is null || ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
            {
                services.AddHostedService<NetworkTopologyRebuildReconcilerBackgroundService>();
            }
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobTerminalCallback, NetworkTopologyRebuildJobTerminalCallback>());
        }

        return services;
    }
}
