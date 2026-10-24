// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Io.Export;
using Honua.Migration;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Routing;
using Honua.Server.Features.Orchestration;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Control-plane event Lambdas share the server image but their Npgsql pool is capped at 2
/// and released when the invocation ends. The database polling loops that the API host keeps
/// must not be hosted there (honua-server#6515).
/// </summary>
public sealed class ControlPlaneEventHandlerDatabasePollingTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void EventHandler_DoesNotHostTheDatabasePollingLoops()
    {
        var services = Compose(eventHandler: true);

        Hosts<JobExecutionService>(services).Should().BeFalse();
        Hosts<NetworkTopologyRebuildReconcilerBackgroundService>(services).Should().BeFalse();
        Hosts<MigrationBatchBackgroundService>(services).Should().BeFalse();
        Hosts<GeoservicesImportBackgroundService>(services).Should().BeFalse();
        Hosts<GeoServerImportBackgroundService>(services).Should().BeFalse();
        Hosts<ExportBackgroundService>(services).Should().BeFalse();
        Hosts<OpsHealthRollupSampler>(services).Should().BeFalse();
        Hosts<WorkflowEventTriggerBackgroundService>(services).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ApiHost_StillHostsTheDatabasePollingLoops()
    {
        var services = Compose(eventHandler: false);

        Hosts<JobExecutionService>(services).Should().BeTrue();
        Hosts<NetworkTopologyRebuildReconcilerBackgroundService>(services).Should().BeTrue();
        Hosts<MigrationBatchBackgroundService>(services).Should().BeTrue();
        Hosts<GeoservicesImportBackgroundService>(services).Should().BeTrue();
        Hosts<GeoServerImportBackgroundService>(services).Should().BeTrue();
        Hosts<ExportBackgroundService>(services).Should().BeTrue();
        Hosts<OpsHealthRollupSampler>(services).Should().BeTrue();
        Hosts<WorkflowEventTriggerBackgroundService>(services).Should().BeTrue();
    }

    private static ServiceCollection Compose(bool eventHandler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = eventHandler ? "scheduled-tick" : null,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IJobQueue>(_ => throw new InvalidOperationException("not resolved"));
        services.AddSingleton<INetworkTopologyRebuildStore>(_ => throw new InvalidOperationException("not resolved"));
        services.AddSingleton<IExecutionJobStore>(_ => throw new InvalidOperationException("not resolved"));
        services.AddSingleton<WorkflowOrchestrationEngine>(_ => throw new InvalidOperationException("not resolved"));
        services.AddJobWorker(configuration);
        services.AddNetworkTopologyRebuildJobs(configuration);
        services.AddHonuaImportExportAndTileOperations(configuration);
        services.AddObservability(configuration);
        services.AddOrchestrationBackgroundServices(configuration);
        return services;
    }

    private static bool Hosts<TService>(IServiceCollection services)
        => services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(TService));
}
