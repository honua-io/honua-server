// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// The per-invocation decisions <see cref="ControlPlaneLambdaEventTargets"/> makes on a host with and
/// without the Redis-backed durable control plane. On honua-release run 38242272260 (aws-serverless,
/// Redis-off) the targets were not registered at all and every event answered 500.
/// </summary>
public sealed class ControlPlaneLambdaEventTargetsTests
{
    [UnitTest]
    public void Registration_IsIndependentOfTheDurableControlPlane()
    {
        using var provider = RedisOffProvider();

        provider.GetService<IControlPlaneLambdaEventTargets>().Should().NotBeNull();
        provider.GetService<IScheduledTickDispatcher>().Should().NotBeNull();
    }

    [UnitTest]
    public void Registration_IsIdempotent()
    {
        var services = new ServiceCollection()
            .AddHonuaControlPlaneLambdaEventTargets()
            .AddHonuaControlPlaneLambdaEventTargets();

        services.Count(descriptor => descriptor.ServiceType == typeof(IControlPlaneLambdaEventTargets)).Should().Be(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(IScheduledTickDispatcher)).Should().Be(1);
    }

    [UnitTest]
    public async Task RedisOff_BatchEventReconcile_RefusesWithMissingRedis()
    {
        using var provider = RedisOffProvider();
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        var act = () => targets.ReconcileBatchJobAsync("batch-job-1", CancellationToken.None);

        (await act.Should().ThrowAsync<ControlPlaneEventDependencyUnavailableException>())
            .Which.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [UnitTest]
    public async Task RedisOff_Backstop_RefusesWithMissingRedis()
    {
        using var provider = RedisOffProvider();
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        var act = () => targets.RunBackstopAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ControlPlaneEventDependencyUnavailableException>())
            .Which.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [UnitTest]
    public async Task RedisOff_ComposedRedisIndependentTick_Runs()
    {
        var tick = new CountingTickHandler(ScheduledTickKind.TemporaryFileCleanup);
        using var provider = RedisOffProvider(tick);
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        await targets.RunScheduledTickAsync(ScheduledTickKind.TemporaryFileCleanup, CancellationToken.None);

        tick.Runs.Should().Be(1);
    }

    [UnitTheory]
    [InlineData(ScheduledTickKind.WorkflowSchedule)]
    [InlineData(ScheduledTickKind.JobReconciliation)]
    [InlineData(ScheduledTickKind.GeoprocessingOutputSweep)]
    public async Task RedisOff_DurableStoreTick_RefusesWithMissingRedis(ScheduledTickKind kind)
    {
        using var provider = RedisOffProvider();
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        var act = () => targets.RunScheduledTickAsync(kind, CancellationToken.None);

        var refusal = (await act.Should().ThrowAsync<ControlPlaneEventDependencyUnavailableException>()).Which;
        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
        refusal.Subject.Should().Be(kind.ToString());
    }

    [UnitTest]
    public async Task RedisOff_UncomposedRedisIndependentTick_KeepsTheKindUnavailableContract()
    {
        // Digest flush is not a Redis tick; when its feature is not composed the route answers the
        // existing scheduled_tick_kind_unavailable 503, not a Redis refusal.
        using var provider = RedisOffProvider();
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        var act = () => targets.RunScheduledTickAsync(ScheduledTickKind.DigestFlush, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public async Task RedisOn_MissingDurableStoreTick_IsNotBlamedOnRedis()
    {
        var services = new ServiceCollection().AddOptions();
        // Only the descriptor matters: the targets ask the catalog whether the graph is composed.
        services.AddSingleton<ControlPlaneEventHandler>(_ => throw new InvalidOperationException("not resolved by this test"));
        services.AddHonuaControlPlaneLambdaEventTargets();
        using var provider = services.BuildServiceProvider();
        var targets = provider.GetRequiredService<IControlPlaneLambdaEventTargets>();

        var act = () => targets.RunScheduledTickAsync(ScheduledTickKind.JobReconciliation, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void DurableStoreTickKinds_AreExactlyTheRedisGatedHandlers()
        => ControlPlaneLambdaEventTargets.DurableStoreTickKinds.Should().BeEquivalentTo(
        [
            ScheduledTickKind.WorkflowSchedule,
            ScheduledTickKind.JobReconciliation,
            ScheduledTickKind.GeoprocessingOutputSweep,
        ]);

    private static ServiceProvider RedisOffProvider(params IScheduledTickHandler[] handlers)
    {
        var services = new ServiceCollection().AddOptions();
        foreach (var handler in handlers)
        {
            services.AddSingleton<IScheduledTickHandler>(handler);
        }

        return services.AddHonuaControlPlaneLambdaEventTargets().BuildServiceProvider();
    }

    private sealed class CountingTickHandler(ScheduledTickKind kind) : IScheduledTickHandler
    {
        public int Runs { get; private set; }

        public ScheduledTickKind Kind => kind;

        public Task RunTickAsync(CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }
}
