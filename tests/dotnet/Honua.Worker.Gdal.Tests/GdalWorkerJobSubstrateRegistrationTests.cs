// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

// The worker reads its jobs from the serving host's system database and from nothing else.
public sealed class GdalWorkerJobSubstrateRegistrationTests
{
    [UnitTest]
    public void AddGdalWorker_WithSystemDatabaseAndNoRedis_ResolvesTheJobSubstrateFromPostgres()
    {
        var services = ComposeWorker();
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = services.BuildServiceProvider();

        AssertStoreClockAndCandidateSourceAreTheJobStore(provider);
        var queue = provider.GetRequiredService<IJobQueue>().Should().BeOfType<PostgresJobQueue>().Subject;
        provider.GetRequiredService<IQueueClaimReconciler>().Should().BeSameAs(queue);
        provider.GetRequiredService<IExecutionLogStore>().Should().BeOfType<PostgresExecutionLogStore>();
        HostedImplementations(services).Should().Contain(
            [typeof(JobExecutionService), typeof(JobReconciliationService)]);
    }

    // AssignedExecutionJobQueue takes the store clock as an optional argument and reads the node
    // clock without it, so a worker that lost the registration would still start.
    [UnitTest]
    public void AddGdalWorker_ThenAssignedExecutionWorker_KeepsTheStoreClockAndClaimsOnlyItsOperation()
    {
        var services = ComposeWorker();

        services.AddAssignedExecutionWorker(new ExecutionWorkerLaunch
        {
            OperationId = "native-assigned",
            JobKind = ExecutionJobKind.Geoprocessing,
            RuntimeProfile = RuntimeProfiles.Native,
        });

        using var provider = services.BuildServiceProvider();
        AssertStoreClockAndCandidateSourceAreTheJobStore(provider);
        provider.GetRequiredService<IJobQueue>().Should().BeOfType<AssignedExecutionJobQueue>();
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IQueueClaimReconciler));
        HostedImplementations(services).Should().NotContain(typeof(JobReconciliationService));
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("  ")]
    public void AddGdalWorker_WithoutTheSystemConnectionString_RefusesNamingTheSetting(string? connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var register = () => services.AddGdalWorker(Configuration(connectionString, redis: "localhost:6379"));

        register.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:DefaultConnection*");
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IJobQueue));
    }

    // JobReconciliationService takes the store clock and the candidate source as optional arguments
    // and falls back to the node clock and a scan of every active job, so nothing fails at startup
    // when a registration is lost.
    private static void AssertStoreClockAndCandidateSourceAreTheJobStore(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<IExecutionJobStore>().Should().BeOfType<PostgresExecutionJobStore>().Subject;
        provider.GetService<IJobStoreClock>().Should().BeSameAs(
            store, "expiry is judged on the database clock, never on a node's");
        provider.GetService<IReconcilableJobSource>().Should().BeSameAs(
            store, "reconciliation reads its candidates from the store's index");
    }

    // Nothing connects to the database: no statement runs.
    private static ServiceCollection ComposeWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGdalWorker(Configuration("Host=localhost;Database=unused", redis: null));
        return services;
    }

    private static IConfiguration Configuration(string? systemConnectionString, string? redis)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = systemConnectionString,
            ["ConnectionStrings:redis"] = redis,
            ["Licensing:Mode"] = "Disabled",
        }).Build();

    private static Type[] HostedImplementations(IServiceCollection services)
        => services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType is not null)
            .Select(static descriptor => descriptor.ImplementationType!)
            .ToArray();
}
