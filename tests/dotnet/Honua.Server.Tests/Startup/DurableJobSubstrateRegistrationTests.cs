// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Server.Tests.Startup;

// The job store, queue, log store and result packages live in the system database, so a host
// composes them from its NpgsqlDataSource and from nothing else.
public sealed class DurableJobSubstrateRegistrationTests
{
    private static readonly Type[] _jobSubstrateServices =
    [
        typeof(IExecutionJobStore),
        typeof(IJobStoreClock),
        typeof(IReconcilableJobSource),
        typeof(IJobQueue),
        typeof(IQueueClaimReconciler),
        typeof(IExecutionLogStore),
        typeof(IGeoprocessingResultPackageStore),
    ];

    public static TheoryData<bool, bool, DurableJobSubstrateCause?> RedisStates => new()
    {
        { false, false, null },
        { true, false, null },
        { true, true, DurableJobSubstrateCause.RedisUnreachable },
        { true, true, DurableJobSubstrateCause.RedisPersistenceDisabled },
        { true, true, null },
    };

    public static TheoryData<bool, bool, DurableJobSubstrateCause?, DurableJobSubstrateCause> RedisCauses => new()
    {
        { false, true, null, DurableJobSubstrateCause.RedisNotConfigured },
        { true, false, null, DurableJobSubstrateCause.RedisNotEntitled },
        { true, true, DurableJobSubstrateCause.RedisUnreachable, DurableJobSubstrateCause.RedisUnreachable },
        { true, true, DurableJobSubstrateCause.RedisPersistenceDisabled, DurableJobSubstrateCause.RuntimeIncomplete },
        { true, true, null, DurableJobSubstrateCause.RuntimeIncomplete },
    };

    [UnitTest]
    public void ServingHost_WithSystemDatabaseAndNoRedis_ResolvesTheJobSubstrateFromPostgres()
    {
        var services = ComposeServingHost(withSystemDatabase: true);
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = BuildProvider(services);

        AssertStoreClockAndCandidateSourceAreTheJobStore(provider);
        var queue = provider.GetRequiredService<IJobQueue>().Should().BeOfType<PostgresJobQueue>().Subject;
        provider.GetRequiredService<IQueueClaimReconciler>().Should().BeSameAs(queue);
        provider.GetRequiredService<IExecutionLogStore>().Should().BeOfType<PostgresExecutionLogStore>();
        provider.GetRequiredService<IGeoprocessingResultPackageStore>()
            .Should().BeOfType<PostgresGeoprocessingResultPackageStore>();
        HostedImplementations(services).Should().Contain(typeof(JobExecutionService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(JobReconciliationService));
    }

    [UnitTest]
    public void ServingHost_WithoutSystemDatabase_RegistersNoJobSubstrate()
    {
        var services = ComposeServingHost(withSystemDatabase: false);

        foreach (var service in _jobSubstrateServices)
        {
            services.Should().NotContain(
                descriptor => descriptor.ServiceType == service,
                $"{service.Name} needs the system database");
        }

        HostedImplementations(services).Should().NotContain(typeof(JobExecutionService));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(JobReconciliationService));
    }

    // AssignedExecutionJobQueue takes the store clock as an optional argument and reads the node
    // clock without it, so a worker that lost the registration would still start.
    [UnitTest]
    public void ExecutionWorker_AfterTheWorkerProfile_KeepsTheStoreClockAndClaimsOnlyItsOperation()
    {
        var services = ComposeServingHost(withSystemDatabase: true);

        services.ApplyExecutionWorkerProfile(new ExecutionWorkerLaunch { OperationId = "gp-assigned" });

        using var provider = BuildProvider(services);
        AssertStoreClockAndCandidateSourceAreTheJobStore(provider);
        provider.GetRequiredService<IJobQueue>().Should().BeOfType<AssignedExecutionJobQueue>();
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(PostgresJobQueue));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IQueueClaimReconciler));
    }

    [UnitTest]
    public void ExecutionJobReconciliation_OnAHostWithTheJobStore_RegistersTheReconcilerAndBackstopSweep()
    {
        var services = ComposeServingHost(withSystemDatabase: true);

        services.AddExecutionJobReconciliation();
        services.AddExecutionJobReconciliationLoops(new ConfigurationBuilder().Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IExecutionJobReconciler)
            && descriptor.ImplementationType == typeof(ExecutionJobReconciler));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ExecutionJobBackstopSweepService));
        HostedImplementations(services).Should().Contain(
        [
            typeof(ExecutionJobReconcilerBackgroundService),
            typeof(ExecutionJobBackstopSweepService),
            typeof(ExecutionQueueDepthCollectorBackgroundService),
            typeof(JobStoreRetentionSweepService),
        ]);
    }

    [UnitTest]
    public void ExecutionJobReconciliation_OnAHostWithTheJobStore_RegistersTheRetentionSweepAsAScheduledTick()
    {
        var services = ComposeServingHost(withSystemDatabase: true);

        services.AddExecutionJobReconciliation();
        using var provider = BuildProvider(services);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IScheduledTickHandler)
            && descriptor.ImplementationType == typeof(JobStoreRetentionScheduledTickHandler));
        ActivatorUtilities.CreateInstance<JobStoreRetentionScheduledTickHandler>(provider)
            .Kind.Should().Be(ScheduledTickKind.JobStoreRetention);
    }

    [UnitTheory]
    [MemberData(nameof(RedisStates))]
    public void Classify_StoreAndQueuePresent_IsAvailableWhateverTheRedisState(
        bool redisConfigured,
        bool redisEntitled,
        DurableJobSubstrateCause? redisFailure)
        => RedisState(redisConfigured, redisEntitled, redisFailure)
            .Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available);

    [UnitTheory]
    [MemberData(nameof(RedisCauses))]
    public void Classify_WithoutTheStoreOrTheQueue_ReportsTheRedisCause(
        bool redisConfigured,
        bool redisEntitled,
        DurableJobSubstrateCause? redisFailure,
        DurableJobSubstrateCause expected)
    {
        var options = RedisState(redisConfigured, redisEntitled, redisFailure);

        options.Classify(jobStorePresent: false, jobQueuePresent: false).Should().Be(expected);
        options.Classify(jobStorePresent: true, jobQueuePresent: false).Should().Be(
            expected, "a store without a queue persists submissions that never drain");
        options.Classify(jobStorePresent: false, jobQueuePresent: true).Should().Be(expected);
    }

    [UnitTest]
    public void EnsureSatisfied_RequireDurableStoreWithRejectedAttestation_ThrowsTypedStartupError()
    {
        var options = RedisState(
            redisConfigured: true, redisEntitled: true, DurableJobSubstrateCause.RedisPersistenceDisabled);

        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            options,
            requireDurableStore: true,
            detail: "appendonly=no, aof_enabled=0");

        var thrown = act.Should().Throw<DurableJobSubstrateNotAttestedException>(
            "an operator who required durability must get a typed refusal, never an "
            + "unresolved-service crash").Subject.Single();

        thrown.Cause.Should().Be(DurableJobSubstrateCause.RedisPersistenceDisabled);
        thrown.Detail.Should().Be("appendonly=no, aof_enabled=0");
        thrown.Remediation.Should().Contain("appendonly yes");
        DurableJobSubstrateNotAttestedException.CapabilityId.Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        thrown.Message.Should().Contain("Jobs:RequireDurableStore");
        thrown.Message.Should().Contain("appendonly=no, aof_enabled=0");
    }

    [UnitTest]
    public void EnsureSatisfied_RejectedAttestationWithoutRequireFlag_Degrades()
    {
        var options = RedisState(
            redisConfigured: true, redisEntitled: true, DurableJobSubstrateCause.RedisPersistenceDisabled);

        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            options,
            requireDurableStore: false);

        act.Should().NotThrow("a rejected attestation is information unless the operator required durability");
    }

    [UnitTest]
    public void EnsureSatisfied_RequireDurableStoreWithAcceptedAttestation_DoesNotThrow()
    {
        var options = new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityAttestation = new RedisDurabilityAttestation(
                "redis:6379",
                "aof (appendonly=yes, aof_enabled=1)",
                "appendfsync=everysec",
                "noeviction",
                DateTimeOffset.UtcNow),
        };

        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(options, requireDurableStore: true);

        act.Should().NotThrow();
    }

    // JobReconciliationService, ExecutionJobReconciler and ExecutionJobReconcilerBackgroundService
    // take the store clock and the candidate source as optional arguments and fall back to the node
    // clock and a scan of every active job, so nothing fails at startup when a registration is lost.
    private static void AssertStoreClockAndCandidateSourceAreTheJobStore(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<IExecutionJobStore>().Should().BeOfType<PostgresExecutionJobStore>().Subject;
        provider.GetService<IJobStoreClock>().Should().BeSameAs(
            store, "expiry is judged on the database clock, never on a node's");
        provider.GetService<IReconcilableJobSource>().Should().BeSameAs(
            store, "reconciliation reads its candidates from the store's index");
    }

    // Composes the job substrate in the order the serving composition root does. Nothing connects
    // to the database: no statement runs.
    private static ServiceCollection ComposeServingHost(bool withSystemDatabase)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (withSystemDatabase)
        {
            services.AddSingleton(_ => NpgsqlDataSource.Create("Host=localhost;Database=unused"));
        }

        var configuration = new ConfigurationBuilder().Build();
        services.AddGeoprocessing(configuration);
        services.AddJobOrchestration();
        services.AddJobWorker(configuration);
        return services;
    }

    private static DurableJobSubstrateOptions RedisState(
        bool redisConfigured,
        bool redisEntitled,
        DurableJobSubstrateCause? redisFailure)
        => new()
        {
            RedisConfigured = redisConfigured,
            RedisEntitled = redisEntitled,
            RedisDurabilityFailure = redisFailure,
        };

    private static Type[] HostedImplementations(IServiceCollection services)
        => services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType is not null)
            .Select(static descriptor => descriptor.ImplementationType!)
            .ToArray();

    // Not ValidateOnBuild: AddGeoprocessing contributes descriptors that need the rest of the
    // server composition, which these tests do not stand up.
    private static ServiceProvider BuildProvider(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
}
