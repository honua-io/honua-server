// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression coverage for honua-server#4502: a Redis whose durability attestation is REJECTED
/// must still compose <see cref="IExecutionJobStore"/>, because every consumer of it is
/// registered unconditionally on <c>connectedRedis != null</c> in the composition root.
/// </summary>
/// <remarks>
/// <para>
/// The crash this pins is a <em>registration-contract</em> break, not a lifetime break: #4141
/// gated the durable job store on an accepted <c>RedisDurabilityAttestation</c> while leaving
/// <c>ExecutionJobReconciler</c>, <c>ControlPlaneEventHandler</c>,
/// <c>ExecutionJobBackstopSweepService</c>, <c>ExecutionJobReconcilerBackgroundService</c> and
/// <c>MetadataReleaseReconciler</c> registered regardless. Against a stock <c>redis:7-alpine</c>
/// (<c>appendonly no</c>) the attestation rejects with
/// <see cref="DurableJobSubstrateCause.RedisPersistenceDisabled"/>, so
/// <c>ServiceProvider</c> descriptor validation threw 31 times with
/// <c>Unable to resolve service for type 'IExecutionJobStore'</c> and the container exited 139
/// before binding a port — the whole honua-release Slice-1 candidate stack went BLOCKED on
/// "server not ready at http://localhost:8080".
/// </para>
/// <para>
/// These are <c>ValidateOnBuild</c> tests for the same reason
/// <c>ImportExportTileOperationsRegistrationTests</c> is: the harness runs
/// <c>ASPNETCORE_ENVIRONMENT=Development</c>, where ASP.NET turns descriptor validation ON, and
/// a lazy resolve only covers the services a test happens to ask for.
/// </para>
/// </remarks>
public sealed class DurableJobSubstrateRegistrationTests
{
    [UnitTest]
    public void AddGeoprocessing_RedisWithoutAttestedDurability_StillRegistersExecutionJobStore()
    {
        var services = ComposeJobSubstrate(attested: false);

        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(IExecutionJobStore),
            "durability attestation decides what the server ADVERTISES, never whether the "
            + "execution-job store is resolvable (honua-server#4502)");
    }

    [UnitTest]
    public void AddGeoprocessing_RedisWithoutAttestedDurability_ExecutionJobConsumerActivates()
    {
        var services = ComposeJobSubstrate(attested: false);
        RegisterExecutionJobConsumer(services);
        using var provider = BuildProvider(services);

        var resolve = () => provider.GetRequiredService<IExecutionJobReconciler>();

        resolve.Should().NotThrow(
            "the reconciler is registered unconditionally by the composition root, so the "
            + "store it injects must resolve on a non-attested Redis too — this activation is "
            + "the first of the 31 that threw 'Unable to resolve service for type "
            + "IExecutionJobStore' and killed the process at startup (honua-server#4502)")
            .Subject.Should().NotBeNull();
    }

    [UnitTest]
    public void AddGeoprocessing_RedisWithAttestedDurability_RegistersDurableStore()
    {
        var services = ComposeJobSubstrate(attested: true);
        RegisterExecutionJobConsumer(services);
        using var provider = BuildProvider(services);

        provider.GetRequiredService<IExecutionJobStore>().Should().NotBeNull(
            "the attested durable path from #4141 is unchanged");
        provider.GetRequiredService<IJobQueue>().Should().NotBeNull();
        provider.GetRequiredService<IExecutionJobReconciler>().Should().NotBeNull();
    }

    /// <summary>
    /// The complementary half of the matrix: with no Redis at all there is no execution-job
    /// substrate to compose and none of its consumers are registered either, so the stores-less
    /// dev/test profile keeps its existing shape. This is what stops the #4502 fix from being
    /// "register something unconditionally", which would switch on the Redis-gated tile-cache and
    /// provisioner submission services against a queue that does not exist.
    /// </summary>
    [UnitTest]
    public void AddGeoprocessing_WithoutRedis_DoesNotRegisterExecutionJobStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeoprocessing(new ConfigurationBuilder().Build());
        services.AddJobOrchestration();

        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IJobQueue));
    }

    [UnitTest]
    public void EnsureSatisfied_RequireDurableStoreWithRejectedAttestation_ThrowsTypedStartupError()
    {
        var options = new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityFailure = DurableJobSubstrateCause.RedisPersistenceDisabled,
        };

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
        var options = new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityFailure = DurableJobSubstrateCause.RedisPersistenceDisabled,
        };

        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            options,
            requireDurableStore: false);

        act.Should().NotThrow("degrade-not-crash is the default startup policy (#4502)");
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

    [UnitTest]
    public void AddGeoprocessing_ConnectedButUnentitledRedis_DoesNotRegisterExecutionJobStore()
    {
        // Infrastructure Redis is connected in every non-Development/Test deployment regardless of
        // entitlement (requiresDurableDistributedEvents), so the multiplexer alone must never
        // compose durable jobs — otherwise an unentitled deployment runs them.
        var services = ComposeJobSubstrate(attested: true, entitled: false);

        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IExecutionJobStore),
            "entitlement is a separate gate from durability attestation (honua-server#4502)");
    }

    [UnitTest]
    public void Classify_UnentitledRedisThatAttests_ReportsNotEntitledRatherThanAvailable()
    {
        // The regression this pins: durability is inspected against whatever infrastructure Redis
        // is connected, so an UNENTITLED deployment whose Redis does attest would otherwise be
        // classified Available and advertise 'jobs.runner'.
        var options = new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = false,
            RedisDurabilityAttestation = new RedisDurabilityAttestation(
                "redis:6379",
                "aof (appendonly=yes, aof_enabled=1)",
                "appendfsync=everysec",
                "noeviction",
                DateTimeOffset.UtcNow),
        };

        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.RedisNotEntitled);
    }

    [UnitTest]
    public void Classify_EntitledRedisThatAttests_StaysAvailable()
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

        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available);
    }

    /// <summary>
    /// Composes the durable job substrate the way the server's composition root does when Redis
    /// is connected: <c>AddGeoprocessing</c> contributes the store, <c>AddJobOrchestration</c>
    /// composes the queue and log store around it. <paramref name="attested"/> mirrors whether
    /// <c>Program.cs</c> published the accepted attestation as a resolvable singleton;
    /// <paramref name="entitled"/> mirrors the separate <see cref="DurableJobSubstrateEntitlement"/>
    /// marker it publishes when the bootstrap license grants <c>caching.redis</c>. The two are
    /// deliberately independent knobs — that is the honua-server#4502 contract.
    /// </summary>
    private static ServiceCollection ComposeJobSubstrate(bool attested, bool entitled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        if (entitled)
        {
            services.AddSingleton(new DurableJobSubstrateEntitlement());
        }

        if (attested)
        {
            services.AddSingleton(new RedisDurabilityAttestation(
                "redis:6379",
                "aof (appendonly=yes, aof_enabled=1)",
                "appendfsync=everysec",
                "noeviction",
                DateTimeOffset.UtcNow));
        }

        services.AddGeoprocessing(new ConfigurationBuilder().Build());
        services.AddJobOrchestration();
        return services;
    }

    /// <summary>
    /// Registers the real <c>ExecutionJobReconciler</c> — the first of the 31 consumers named in
    /// the #4502 stack trace — with the collaborators the composition root gives it, so
    /// <c>ValidateOnBuild</c> walks a constructor graph that genuinely requires
    /// <see cref="IExecutionJobStore"/>.
    /// </summary>
    private static void RegisterExecutionJobConsumer(IServiceCollection services)
    {
        services.AddSingleton(Substitute.For<IUniversalProgressStore>());
        services.AddSingleton<IExecutionJobReconciler, ExecutionJobReconciler>();
    }

    /// <summary>
    /// Builds the container with scope validation on, then lets each test resolve the specific
    /// consumer it cares about.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>ValidateOnBuild</c>: that walks every descriptor
    /// <c>AddGeoprocessing</c> contributes, most of which need the rest of the server composition
    /// (authorization evaluators, metadata graph, file storage) that this focused fixture has no
    /// business standing up. The #4502 failure is a resolution failure on one well-known
    /// constructor graph, so resolving that graph reproduces it exactly — with the same
    /// <c>Unable to resolve service for type 'IExecutionJobStore'</c> message the container
    /// emitted 31 times at boot — without pulling in unrelated modules.
    /// </remarks>
    private static ServiceProvider BuildProvider(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
}
