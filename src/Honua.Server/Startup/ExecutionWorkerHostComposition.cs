// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Infrastructure.Licensing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Startup;

// The worker drops every hosted loop it must not run, binds no port, and can claim only the operation it was launched for.
internal static class ExecutionWorkerHostComposition
{
    internal static readonly IReadOnlyList<Type> RemovedServiceTypes =
    [
        typeof(IBatchComputeBackend),
        typeof(IDeployBackend),
        typeof(IExecutionJobReconciler),
        typeof(IWorkflowOperationReconciler),
        typeof(IMetadataReleaseOperationReconciler),
        typeof(ICoordinatedReleaseReconciler),
        typeof(ICoordinatedContainerStepExecutor),
        typeof(ICoordinatedMetadataStepExecutor),
        typeof(WorkflowOperationBackstopSweepService),
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

        return services.AddAssignedExecutionWorker(launch);
    }

    internal static bool IsRetainedHostedService(ServiceDescriptor descriptor)
    {
        var implementationType = descriptor.IsKeyedService ? null : descriptor.ImplementationType;
        return implementationType?.Namespace?.StartsWith("OpenTelemetry", StringComparison.Ordinal) ?? false;
    }
}

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
