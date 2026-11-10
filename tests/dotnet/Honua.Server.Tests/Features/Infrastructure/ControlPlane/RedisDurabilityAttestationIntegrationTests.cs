// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Executes the Redis durability attestation against real Redis containers, for the control plane
/// that is stored in Redis: operation proposals. The attestation is information. Its outcome is
/// published as <see cref="DurableJobSubstrateOptions.RedisDurabilityStatus"/> and on the Redis
/// health check, the proposal store serves whatever the outcome, and only
/// <c>Jobs:RequireDurableStore</c> turns a non-attested outcome into a startup refusal. The
/// recovery case kills the container process abruptly so graceful shutdown cannot stand in for
/// AOF recovery evidence.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisDurabilityAttestationIntegrationTests
{
    [IntegrationTest]
    public async Task NonPersistentRedis_IsRejectedAndStillServesProposals()
    {
        await using var redis = await StartRedisAsync(appendOnly: false, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisPersistenceDisabled, inspection.FailureDetail);
        AssertPublishedAsInformation(inspection, RedisDurabilityStatuses.NotDurable);
        await AssertProposalStoreServesAsync(redis.Multiplexer);
    }

    [IntegrationTest]
    public async Task EvictingRedis_IsRejectedAndStillServesProposals()
    {
        await using var redis = await StartRedisAsync(appendOnly: true, evictionPolicy: "allkeys-lru");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisEvictionPolicyUnsafe, inspection.FailureDetail);
        AssertPublishedAsInformation(inspection, RedisDurabilityStatuses.NotDurable);
        await AssertProposalStoreServesAsync(redis.Multiplexer);
    }

    [IntegrationTest]
    public async Task UnsafeFsyncRedis_IsRejectedAndStillServesProposals()
    {
        await using var redis = await StartRedisAsync(
            appendOnly: true,
            evictionPolicy: "noeviction",
            appendFsync: "no");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisWritePolicyUnsafe, inspection.FailureDetail);
        AssertPublishedAsInformation(inspection, RedisDurabilityStatuses.NotDurable);
        await AssertProposalStoreServesAsync(redis.Multiplexer);
    }

    // The managed-Redis shape: AWS ElastiCache and MemoryDB block CONFIG.
    [IntegrationTest]
    public async Task RedisWithoutPolicyReadPermission_IsUnverifiedAndStillServesProposals()
    {
        await using var redis = await StartRedisAsync(
            appendOnly: true,
            evictionPolicy: "noeviction",
            denyPolicyInspection: true);
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisAttestationUnavailable, inspection.FailureDetail);
        AssertPublishedAsInformation(inspection, RedisDurabilityStatuses.Unverified);
        await AssertProposalStoreServesAsync(redis.Multiplexer);
    }

    [IntegrationTest]
    public async Task NonPersistentRedis_HealthCheckStaysHealthyAndReportsDurabilityAsInformation()
    {
        await using var redis = await StartRedisAsync(appendOnly: false, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);
        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisPersistenceDisabled, inspection.FailureDetail);
        using var cacheProvider = BuildRedisCache(redis.ConnectionString);
        var healthCheck = CreateHealthCheck(redis.Multiplexer, cacheProvider, Published(inspection));

        var health = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        health.Status.Should().Be(HealthStatus.Healthy);
        health.Data["durability"].Should().Be(RedisDurabilityStatuses.NotDurable);
        health.Data["cause"].Should().Be(nameof(DurableJobSubstrateCause.RedisPersistenceDisabled));
        health.Data["durabilityAttested"].Should().Be(false);
        health.Data["remediation"].Should().Be(
            DurableJobSubstrateRemediation.For(DurableJobSubstrateCause.RedisPersistenceDisabled));
        health.Data["consequence"].Should().Be(DurableJobSubstrateRemediation.NonDurableConsequence);
    }

    // The health check proves connectivity before it reports the durability outcome, so an
    // operator whose Redis has AOF off is told about an outage and not about durability.
    [IntegrationTest]
    public async Task RejectedDurability_WithUnreachableRedis_ReportsTheOutageNotTheDurabilityVerdict()
    {
        await using var redis = await StartRedisAsync(appendOnly: false, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);
        inspection.Accepted.Should().BeFalse();
        using var cacheProvider = BuildRedisCache(redis.ConnectionString);
        var healthCheck = CreateHealthCheck(redis.Multiplexer, cacheProvider, Published(inspection));

        var serving = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        serving.Status.Should().Be(HealthStatus.Healthy);
        serving.Data["durability"].Should().Be(RedisDurabilityStatuses.NotDurable);

        await redis.Container.StopAsync();

        var outage = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        outage.Status.Should().Be(HealthStatus.Unhealthy,
            "a connectivity failure outranks the durability verdict it would otherwise hide");
        outage.Description.Should().NotContain("durability");
    }

    [IntegrationTest]
    public async Task AcceptedRedis_SurvivesAbruptKillAndRestart_WithProposalsIntact()
    {
        await using var redis = await StartRedisAsync(appendOnly: true, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);
        inspection.Accepted.Should().BeTrue();
        inspection.Attestation!.PersistenceMode.Should().Contain("aof_enabled=1");
        inspection.Attestation.AcknowledgedWritePolicy.Should().Be("appendfsync=always");
        inspection.Attestation.EvictionPolicy.Should().Be("noeviction");
        inspection.Attestation.Endpoint.Should().NotContain("@");
        inspection.Attestation.Endpoint.ToLowerInvariant().Should().NotContain("password");
        inspection.Attestation.ObservedAt.Should().BeBefore(DateTimeOffset.UtcNow);

        var published = Published(inspection);
        published.RedisDurabilityStatus.Should().Be(RedisDurabilityStatuses.Attested);
        var requireDurableStore = () => DurableJobSubstrateStartupGate.EnsureSatisfied(published, requireDurableStore: true);
        requireDurableStore.Should().NotThrow();

        using var cacheProvider = BuildRedisCache(redis.ConnectionString);
        var health = await CreateHealthCheck(redis.Multiplexer, cacheProvider, published)
            .CheckHealthAsync(new HealthCheckContext());
        health.Status.Should().Be(HealthStatus.Healthy);
        health.Data["durability"].Should().Be(RedisDurabilityStatuses.Attested);
        health.Data["durabilityEndpoint"].Should().Be(inspection.Attestation.Endpoint);
        health.Data["persistenceMode"].Should().Be(inspection.Attestation.PersistenceMode);
        health.Data["acknowledgedWritePolicy"].Should().Be(inspection.Attestation.AcknowledgedWritePolicy);
        health.Data["evictionPolicy"].Should().Be(inspection.Attestation.EvictionPolicy);
        health.Data["durabilityObservedAt"].Should().Be(inspection.Attestation.ObservedAt);

        var proposals = new RedisOperationProposalStore(redis.Multiplexer, NullLogger<RedisOperationProposalStore>.Instance);
        var awaitingApproval = CreateAwaitingApprovalProposal();
        var rejected = CreateAwaitingApprovalProposal();
        (await proposals.TryCreateAsync(awaitingApproval)).Should().BeTrue();
        (await proposals.TryCreateAsync(rejected)).Should().BeTrue();
        (await proposals.TrySetAsync((await proposals.GetAsync(rejected.ProposalId))! with
        {
            Status = OperationProposalStatus.Rejected,
            ResolvedBy = "approver",
        })).Should().BeTrue();
        var awaitingApprovalBeforeKill = (await proposals.GetAsync(awaitingApproval.ProposalId))!;
        var rejectedBeforeKill = (await proposals.GetAsync(rejected.ProposalId))!;

        await redis.Multiplexer.GetDatabase().PingAsync();
        await KillContainerAsync(redis.Container);
        await redis.Container.StartAsync();
        await WaitForRedisAsync(redis.Multiplexer);

        (await proposals.GetAsync(awaitingApproval.ProposalId)).Should().BeEquivalentTo(awaitingApprovalBeforeKill);
        (await proposals.GetAsync(rejected.ProposalId)).Should().BeEquivalentTo(rejectedBeforeKill);
        rejectedBeforeKill.Status.Should().Be(OperationProposalStatus.Rejected);
        var active = (await proposals.ListActiveAsync()).Select(proposal => proposal.ProposalId).ToArray();
        active.Should().Contain(awaitingApproval.ProposalId);
        active.Should().NotContain(rejected.ProposalId);
    }

    private static DurableJobSubstrateOptions Published(RedisDurabilityAttestationResult inspection) => new()
    {
        RedisConfigured = true,
        RedisEntitled = true,
        RedisDurabilityAttestation = inspection.Attestation,
        RedisDurabilityFailure = inspection.FailureCause,
        RedisDurabilityDetail = inspection.FailureDetail,
    };

    private static void AssertPublishedAsInformation(
        RedisDurabilityAttestationResult inspection,
        string expectedStatus)
    {
        var options = Published(inspection);

        options.RedisDurabilityStatus.Should().Be(expectedStatus);
        var defaultPolicy = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            options, requireDurableStore: false, inspection.FailureDetail);
        defaultPolicy.Should().NotThrow();
        var requireDurableStore = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            options, requireDurableStore: true, inspection.FailureDetail);
        requireDurableStore.Should().Throw<DurableJobSubstrateNotAttestedException>()
            .Which.Cause.Should().Be(inspection.FailureCause);
    }

    private static async Task AssertProposalStoreServesAsync(IConnectionMultiplexer redis)
    {
        var proposals = new RedisOperationProposalStore(redis, NullLogger<RedisOperationProposalStore>.Instance);
        var proposal = CreateAwaitingApprovalProposal();

        (await proposals.TryCreateAsync(proposal)).Should().BeTrue();

        (await proposals.GetAsync(proposal.ProposalId))!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        (await proposals.ListActiveAsync()).Should().Contain(active => active.ProposalId == proposal.ProposalId);
    }

    private static OperationProposal CreateAwaitingApprovalProposal()
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationProposal
        {
            ProposalId = $"durability-{Guid.NewGuid():N}",
            Kind = OperationClass.Deploy,
            Status = OperationProposalStatus.AwaitingApproval,
            RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "durability" },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ServiceProvider BuildRedisCache(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);
        return services.BuildServiceProvider();
    }

    private static Honua.Server.Features.HealthCheck.RedisHealthCheck CreateHealthCheck(
        IConnectionMultiplexer redis,
        ServiceProvider cacheProvider,
        DurableJobSubstrateOptions published)
        => new(
            redis,
            cacheProvider.GetRequiredService<IDistributedCache>(),
            NullLogger<Honua.Server.Features.HealthCheck.RedisHealthCheck>.Instance,
            Options.Create(published));

    private static async Task<RedisTestContainer> StartRedisAsync(
        bool appendOnly,
        string evictionPolicy,
        string appendFsync = "always",
        bool denyPolicyInspection = false)
    {
        var hostPort = ReserveLoopbackPort();
        var command = new List<string>
        {
            "redis-server",
            "--appendonly",
            appendOnly ? "yes" : "no",
                "--appendfsync",
                appendFsync,
                "--save",
            "",
            "--maxmemory-policy",
            evictionPolicy
        };
        if (denyPolicyInspection)
        {
            command.AddRange(["--user", "default", "on", "nopass", "~*", "+@all", "-info", "-config"]);
        }

        var container = new RedisBuilder("redis:7.2-alpine")
            .WithPortBinding(hostPort, 6379)
            .WithCommand(command.ToArray())
            .Build();
        await container.StartAsync();

        var connectionString = container.GetConnectionString();
        var options = ConfigurationOptions.Parse(connectionString, ignoreUnknown: true);
        options.AllowAdmin = true;
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 5;
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisTestContainer(container, connectionString, multiplexer);
    }

    private static async Task KillContainerAsync(RedisContainer container)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            ArgumentList = { "kill", container.Id },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start docker kill.");
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
    }

    private static async Task WaitForRedisAsync(ConnectionMultiplexer multiplexer)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await multiplexer.GetDatabase().PingAsync() < TimeSpan.FromSeconds(5))
                {
                    return;
                }
            }
            catch (RedisConnectionException)
            {
                // The multiplexer is expected to observe the abrupt disconnect before reconnecting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Redis did not reconnect after the abrupt container restart.");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RedisTestContainer(
        RedisContainer container,
        string connectionString,
        ConnectionMultiplexer multiplexer) : IAsyncDisposable
    {
        public RedisContainer Container { get; } = container;
        public string ConnectionString { get; } = connectionString;
        public ConnectionMultiplexer Multiplexer { get; } = multiplexer;

        public ValueTask DisposeAsync()
        {
            Multiplexer.Dispose();
            return Container.DisposeAsync();
        }
    }
}
