// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Infrastructure.Licensing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Startup;

/// <summary>
/// Narrows a fully composed server host to the <see cref="HostCompositionProfile.ExecutionWorker"/>
/// profile: the process a batch compute backend launches for exactly one execution job.
/// </summary>
/// <remarks>
/// <para>
/// The worker runs the generic server image, so it is composed by the same root as the server and
/// therefore resolves every dependency the job's executor needs (database providers, the operation
/// key ring for secret-channel reads, file storage, the durable job store and log store). This
/// pass then removes what a worker must never run:
/// </para>
/// <list type="bullet">
/// <item>every hosted service except the shared <see cref="JobExecutionService"/> loop, license
/// revalidation and telemetry export — the control-plane reconcilers and backstop sweeps (the
/// <c>ExecutionJobReconcilerBackgroundService</c> that dispatched queued jobs to AWS Batch from
/// inside the worker in run 38048901509, EventId 9046), the stale-claim reaper, workflow
/// orchestration, outbox, import/export, alert, audit and cleanup loops;</item>
/// <item>the HTTP server: <see cref="IServer"/> is replaced by <see cref="NoHttpServer"/>, which
/// binds no port, so the worker serves no HTTP surface at all;</item>
/// <item>the batch compute backends (the submission path), the deploy backends, the control-plane
/// reconcilers and reconcile/tick dispatchers, and the proposal gateway with its per-class
/// executors;</item>
/// <item>the shared Redis job queue, replaced by <see cref="AssignedExecutionJobQueue"/>, so the
/// execution loop can claim only the operation this process was launched for.</item>
/// </list>
/// <para>
/// The worker reports the outcome exactly as an in-process run does: <see cref="JobExecutionService"/>
/// finalizes the durable record (succeeded with its artifacts and result package, or failed with
/// the executor's typed error) and the serving host's reconciler observes the terminal record.
/// </para>
/// </remarks>
internal static class ExecutionWorkerHostComposition
{
    /// <summary>
    /// Control-plane service types the execution worker profile never composes.
    /// </summary>
    internal static readonly IReadOnlyList<Type> RemovedServiceTypes =
    [
        typeof(IBatchComputeBackend),
        typeof(IDeployBackend),
        typeof(IExecutionJobReconciler),
        typeof(IWorkflowOperationReconciler),
        typeof(IMetadataReleaseOperationReconciler),
        typeof(ICoordinatedReleaseReconciler),
        typeof(IOperationReconcileDispatcher),
        typeof(IScheduledTickDispatcher),
        typeof(IScheduledTickHandler),
        typeof(ControlPlaneEventHandler),
        typeof(ExecutionJobBackstopSweepService),
        typeof(JobReconciliationService),
        typeof(IOperationGateway),
        typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor),
        typeof(IQueueClaimReconciler),
        typeof(RedisJobQueue),
        typeof(IJobQueue),
    ];

    /// <summary>
    /// Applies the execution worker profile to a composed service collection.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The durable job store is not composed (no Redis connection), so the worker could not report
    /// the job's outcome; the process refuses to start instead of running a job nobody can observe.
    /// </exception>
    public static IServiceCollection ApplyExecutionWorkerProfile(
        this IServiceCollection services,
        ExecutionWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(launch);

        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(IExecutionJobStore)))
        {
            throw new InvalidOperationException(
                $"Execution worker for operation '{launch.OperationId}' cannot start: the durable job store is not "
                + "composed. Configure ConnectionStrings:redis (the same Redis the serving host uses) so the worker "
                + "can claim the job and report its outcome.");
        }

        var executionLoopComposed = services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(JobExecutionService));

        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(IHostedService) && !IsRetainedHostedService(services[index]))
            {
                services.RemoveAt(index);
            }
        }

        foreach (var serviceType in RemovedServiceTypes)
        {
            services.RemoveAll(serviceType);
        }

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(FileBackedLicenseService)))
        {
            services.AddHostedService(static sp => sp.GetRequiredService<FileBackedLicenseService>());
        }

        // The web host service is added by WebApplicationBuilder.Build(), after this pass, so the
        // HTTP surface is removed at the server instead: nothing listens.
        services.Replace(ServiceDescriptor.Singleton<IServer, NoHttpServer>());

        services.AddSingleton(launch);
        services.AddSingleton<ExecutionWorkerExitState>();
        services.AddSingleton<AssignedExecutionJobQueue>();
        services.AddSingleton<IJobQueue>(static sp => sp.GetRequiredService<AssignedExecutionJobQueue>());
        services.TryAddSingleton<ExecutionJobCancellationTokens>();
        if (!executionLoopComposed)
        {
            services.AddHostedService<JobExecutionService>();
        }

        // Registered after the execution loop so the host stops it first on shutdown.
        services.AddHostedService<ExecutionWorkerLifetimeService>();
        return services;
    }

    /// <summary>
    /// The only hosted services a worker keeps: the shared execution loop (fed by the assigned
    /// queue) and OpenTelemetry's provider host so the worker's traces and metrics still export.
    /// License revalidation is re-added explicitly because it is registered through a factory.
    /// </summary>
    internal static bool IsRetainedHostedService(ServiceDescriptor descriptor)
    {
        var implementationType = descriptor.IsKeyedService ? null : descriptor.ImplementationType;
        return implementationType == typeof(JobExecutionService)
            || (implementationType?.Namespace?.StartsWith("OpenTelemetry", StringComparison.Ordinal) ?? false);
    }
}

/// <summary>
/// The execution worker's <see cref="IServer"/>: starts and stops without binding a port.
/// </summary>
internal sealed class NoHttpServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
