// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.ControlPlane;

/// <summary>Shared assigned-operation composition for managed and native worker hosts.</summary>
internal static class ExecutionWorkerHostExtensions
{
    /// <summary>Replaces persistent queue consumption with one fenced assigned operation.</summary>
    internal static IServiceCollection AddAssignedExecutionWorker(
        this IServiceCollection services, ExecutionWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(launch);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(IExecutionJobStore)))
        {
            throw new InvalidOperationException(
                $"Execution worker for operation '{launch.OperationId}' requires a durable job store. "
                + "Configure ConnectionStrings:redis to use the serving host's store.");
        }

        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType == typeof(IHostedService)
                && (descriptor.ImplementationType == typeof(JobExecutionService)
                    || descriptor.ImplementationType == typeof(JobReconciliationService)))
            {
                services.RemoveAt(index);
            }
        }

        services.RemoveAll<IJobQueue>();
        services.RemoveAll<RedisJobQueue>();
        services.RemoveAll<IQueueClaimReconciler>();
        services.AddSingleton(launch);
        services.AddSingleton<ExecutionWorkerExitState>();
        services.AddSingleton<AssignedExecutionJobQueue>();
        services.AddSingleton<IJobQueue>(static sp => sp.GetRequiredService<AssignedExecutionJobQueue>());
        services.TryAddSingleton<ExecutionJobCancellationTokens>();
        services.AddHostedService(static sp =>
        {
            var loop = ActivatorUtilities.CreateInstance<JobExecutionService>(sp);
            loop.RequeueOnShutdown = true;
            return loop;
        });
        // Stop the lifetime service before the execution loop on provider interruption.
        services.AddHostedService<ExecutionWorkerLifetimeService>();
        return services;
    }

    /// <summary>Resolves the durable outcome before disposing the worker's service provider.</summary>
    internal static async Task<int> RunExecutionWorkerAsync(
        this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            var exitState = host.Services.GetRequiredService<ExecutionWorkerExitState>();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            return await exitState.ResolveAsync().ConfigureAwait(false);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
    }
}
