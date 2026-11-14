// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class OrchestrationServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddOrchestration_WithoutSystemDatabase_RegistersNoEngineOrBackgroundServices()
    {
        var services = new ServiceCollection();

        services.AddOrchestration();
        services.AddOrchestrationBackgroundServices(new ConfigurationBuilder().Build());

        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IWorkflowDefinitionStore));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IWorkflowRunStore));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(WorkflowOrchestrationEngine));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IWorkflowCancellationCoordinator));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
    }

    [UnitTest]
    public void AddOrchestration_WithSystemDatabase_RegistersPostgresStoresAndEngine()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create("Host=localhost"));

        services.AddOrchestration();

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(PostgresWorkflowDefinitionStore));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(PostgresWorkflowRunStore));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(WorkflowOrchestrationEngine));
    }
}
