// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseCapacityMeterTests
{
    [UnitTest]
    public async Task RegisterInstance_WhenJoiningWouldExceedBurstCeiling_RefusesNewRegistrationOnly()
    {
        var meter = CreateMeter(maxSustainedUnits: 4m);

        var first = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-a",
            ServingUnits = 5m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var second = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-b",
            ServingUnits = 0.1m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var state = await meter.GetCapacityStateAsync();

        Assert.True(first.IsAccepted);
        Assert.False(second.IsAccepted);
        Assert.Equal(5m, state.CurrentServingUnits);
        Assert.Equal(1, state.LiveInstanceCount);
    }

    [UnitTest]
    public async Task RegisterInstance_WithNonProductionRole_DoesNotCountTowardBand()
    {
        var meter = CreateMeter(maxSustainedUnits: 1m);

        var decision = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "staging-a",
            ServingUnits = 50m,
            DeploymentRole = LicenseDeploymentRole.Staging,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var state = await meter.GetCapacityStateAsync();

        Assert.True(decision.IsAccepted);
        Assert.Equal(0m, state.CurrentServingUnits);
        Assert.Equal(1, state.ExcludedInstanceCount);
    }

    [UnitTest]
    public async Task RegisterInstance_WithSurgeMode_AllowsRegistrationAboveBurstCeiling()
    {
        var meter = CreateMeter(maxSustainedUnits: 1m);

        var surge = await meter.SetSurgeModeAsync(enabled: true, reason: "incident response");
        var decision = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-surge",
            ServingUnits = 10m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.Serverless
        });

        Assert.True(surge.Surge.IsActive);
        Assert.Equal(LicenseCapacityBandState.Surge, surge.State);
        Assert.True(decision.IsAccepted);
        Assert.Equal(10m, decision.State.CurrentServingUnits);
    }

    [UnitTest]
    public async Task GetCapacityState_WithActiveSurge_IncludesActiveWindowInAllowanceAccounting()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var meter = CreateMeter(maxSustainedUnits: 1m, clock);

        await meter.SetSurgeModeAsync(enabled: true, reason: "planned load test");
        clock.Advance(TimeSpan.FromHours(12));
        var state = await meter.GetCapacityStateAsync();

        Assert.True(state.Surge.IsActive);
        Assert.Equal(0.5m, state.Surge.UsedDaysThisYear);
        Assert.Equal(13.5m, state.Surge.RemainingDaysThisYear);
    }

    [UnitTest]
    public async Task ExecuteAsync_WhenRedisConnectionIsTornDownMidRead_KeepsHeartbeatLoopRunning()
    {
        // honua-server#4815: a Redis latency window ended with the client surfacing a torn-down
        // connection as InvalidOperationException rather than a RedisException. The heartbeat loop
        // must degrade to local metering and keep running; a fault out of ExecuteAsync stops the host.
        var redis = CreateTornDownRedis(out var database);
        var meter = CreateMeter(
            maxSustainedUnits: 4m,
            redis: redis,
            registrationEnabled: true,
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        await meter.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.SetMembersAsync)) < 3 &&
                   DateTime.UtcNow < deadline)
            {
                Assert.False(meter.ExecuteTask!.IsCompleted, $"heartbeat loop ended: {meter.ExecuteTask.Exception?.GetBaseException().Message}");
                await Task.Delay(20);
            }

            Assert.False(meter.ExecuteTask!.IsCompleted, $"heartbeat loop ended: {meter.ExecuteTask.Exception?.GetBaseException().Message}");
            Assert.True(
                database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.SetMembersAsync)) >= 3,
                "the heartbeat loop should keep retrying the coordinated meter");
        }
        finally
        {
            await meter.StopAsync(CancellationToken.None);
        }

        Assert.False(meter.ExecuteTask!.IsFaulted, "stopping the meter after a Redis outage must not surface a fault");
    }

    [UnitTest]
    public async Task GetCapacityState_WhenRedisConnectionIsTornDownMidRead_ReportsMeteringGap()
    {
        var meter = CreateMeter(maxSustainedUnits: 4m, redis: CreateTornDownRedis(out _));

        var state = await meter.GetCapacityStateAsync();

        Assert.True(state.MeteringGap);
        Assert.False(state.RedisCoordinated);
        Assert.Equal(LicenseCapacityBandState.MeteringGap, state.State);
    }

    private static IConnectionMultiplexer CreateTornDownRedis(out IDatabase database)
    {
        // The shape StackExchange.Redis surfaces when its socket pipe was completed under a
        // pending read during a timeout window.
        static Task<T> TornDown<T>() => Task.FromException<T>(
            new InvalidOperationException("Reading is not allowed after reader was completed."));

        database = Substitute.For<IDatabase>();
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => TornDown<RedisValue[]>());
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => TornDown<RedisValue>());
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(_ => TornDown<RedisResult>());

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return redis;
    }

    private static LicenseCapacityMeter CreateMeter(
        decimal maxSustainedUnits,
        TimeProvider? timeProvider = null,
        IConnectionMultiplexer? redis = null,
        bool registrationEnabled = false,
        TimeSpan? heartbeatInterval = null)
    {
        var license = new TestLicenseEntitlementService(
            HonuaEdition.Enterprise,
            capacityTerms: new LicenseCapacityTerms
            {
                MaxSustainedServingUnits = maxSustainedUnits,
                AnnualSurgeDays = 14,
                SurgeAllowance = LicenseCapacitySurgeAllowances.Standard
            });
        return new LicenseCapacityMeter(
            license,
            Options.Create(new LicenseCapacityOptions
            {
                RegistrationEnabled = registrationEnabled,
                InstanceId = "local-test",
                ServingUnits = 1m,
                HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15)
            }),
            timeProvider ?? TimeProvider.System,
            NullLogger<LicenseCapacityMeter>.Instance,
            redis);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan timeSpan) => _utcNow += timeSpan;
    }
}
