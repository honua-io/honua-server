// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Hosting.ComponentTests;

/// <summary>Independent coordinator instances share Redis accounting and authoritative cancellation.</summary>
[Collection(AiAdmissionRedisFixture.CollectionName)]
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisAiExecutionAdmissionCoordinatorTests(AiAdmissionRedisFixture fixture)
{
    [IntegrationTest]
    public async Task AcquireAsync_IndependentCoordinators_RaceAllFourScopesOnRealRedis()
    {
        using var firstConnection = await ConnectAsync();
        using var secondConnection = await ConnectAsync();
        var firstStore = new RedisAiExecutionAdmissionStore(firstConnection);
        var secondStore = new RedisAiExecutionAdmissionStore(secondConnection);
        await using var firstProvider = Provider(firstStore);
        await using var secondProvider = Provider(secondStore);
        foreach (var scope in new[] { "deployment", "actor", "cohort", "workflow" })
        {
            var context = Context();
            var policy = Generous() with
            {
                DeploymentAttempts = scope == "deployment" ? 1 : 100,
                ActorAttempts = scope == "actor" ? 1 : 100,
                CohortAttempts = scope == "cohort" ? 1 : 100,
                WorkflowAttempts = scope == "workflow" ? 1 : 100
            };
            await ReadyAsync(firstConnection, firstStore, context.DeploymentId, policy);
            var coordinators = new[] { Coordinator(firstProvider, context.DeploymentId), Coordinator(secondProvider, context.DeploymentId) };
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, 24).Select(async index =>
            {
                await barrier.Task;
                return await AttemptAsync(coordinators[index % 2], context);
            }).ToArray();
            barrier.SetResult();
            var results = await Task.WhenAll(calls);
            try
            {
                results.Count(result => result.Permit is not null).Should().Be(1, scope);
                results.Count(result => result.Code == "budget_exceeded").Should().Be(23, scope);
            }
            finally
            {
                foreach (var result in results)
                {
                    if (result.Permit is { } permit)
                    {
                        await permit.DisposeAsync();
                    }
                }
            }
        }
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_StopFromOtherCoordinator_CancelsBothActivePermitsAndResumeRetainsCharges()
    {
        using var firstConnection = await ConnectAsync();
        using var secondConnection = await ConnectAsync();
        var firstStore = new RedisAiExecutionAdmissionStore(firstConnection);
        var secondStore = new RedisAiExecutionAdmissionStore(secondConnection);
        await using var firstProvider = Provider(firstStore);
        await using var secondProvider = Provider(secondStore);
        var context = Context();
        var ready = await ReadyAsync(firstConnection, firstStore, context.DeploymentId, Generous() with { DeploymentAttempts = 2 });
        var first = Coordinator(firstProvider, context.DeploymentId);
        var second = Coordinator(secondProvider, context.DeploymentId);
        await using var firstPermit = await first.AcquireAsync(context, default);
        await using var secondPermit = await second.AcquireAsync(context, default);
        await firstPermit.BeginInvocationAsync(default);
        await firstPermit.AuthorizeSendAsync(default);
        await secondPermit.BeginInvocationAsync(default);
        await secondPermit.AuthorizeSendAsync(default);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstRegistration = firstPermit.Token.Register(() => firstCancelled.TrySetResult());
        using var secondRegistration = secondPermit.Token.Register(() => secondCancelled.TrySetResult());

        var stopped = await secondStore.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Stop), default);

        stopped.Applied.Should().BeTrue();
        await Task.WhenAll(firstCancelled.Task, secondCancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
        firstPermit.Token.IsCancellationRequested.Should().BeTrue();
        secondPermit.Token.IsCancellationRequested.Should().BeTrue();
        var denied = () => first.AcquireAsync(context, default);
        (await denied.Should().ThrowAsync<AiAdmissionException>()).Which.Code.Should().Be("stopped");
        await firstPermit.DisposeAsync();
        await secondPermit.DisposeAsync();
        var resumed = await firstStore.ApplyControlAsync(Control(stopped.State!, AiAdmissionControlAction.Resume), default);
        resumed.Applied.Should().BeTrue();
        (await denied.Should().ThrowAsync<AiAdmissionException>()).Which.Code.Should().Be("budget_exceeded");
    }

    [IntegrationTest]
    public async Task DisposeAsync_CallerCancellation_ReleasesOnlyItsOwnReservationAcrossCoordinators()
    {
        using var firstConnection = await ConnectAsync();
        using var secondConnection = await ConnectAsync();
        var firstStore = new RedisAiExecutionAdmissionStore(firstConnection);
        var secondStore = new RedisAiExecutionAdmissionStore(secondConnection);
        await using var firstProvider = Provider(firstStore);
        await using var secondProvider = Provider(secondStore);
        var context = Context();
        await ReadyAsync(firstConnection, firstStore, context.DeploymentId, Generous() with { DeploymentConcurrency = 1 });
        var first = Coordinator(firstProvider, context.DeploymentId);
        var second = Coordinator(secondProvider, context.DeploymentId);
        using var caller = new CancellationTokenSource();
        var permit = await first.AcquireAsync(context, caller.Token);
        var blocked = () => second.AcquireAsync(context, default);
        (await blocked.Should().ThrowAsync<AiAdmissionException>()).Which.Code.Should().Be("concurrency_exceeded");

        caller.Cancel();
        await permit.DisposeAsync();
        await using var next = await second.AcquireAsync(context, default);
        await permit.DisposeAsync();

        await next.BeginInvocationAsync(default);
        await next.AuthorizeSendAsync(default);
        await next.EnsureCurrentAsync(default);
        next.Token.IsCancellationRequested.Should().BeFalse();
    }

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse(fixture.ConnectionString);
        options.AllowAdmin = true;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    private static ServiceProvider Provider(IAiExecutionAdmissionStore store)
        => new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    private static AiExecutionAdmissionCoordinator Coordinator(ServiceProvider provider, string deployment)
        => new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AiExecutionAdmissionOptions { DeploymentId = deployment }),
            TimeProvider.System, NullLogger<AiExecutionAdmissionCoordinator>.Instance);

    private static async Task<(IAiExecutionPermit? Permit, string? Code)> AttemptAsync(IAiExecutionAdmission coordinator, AiExecutionContext context)
    {
        try
        {
            return (await coordinator.AcquireAsync(context, default), null);
        }
        catch (AiAdmissionException exception)
        {
            return (null, exception.Code);
        }
    }

    private static AiExecutionContext Context() => AiExecutionContext.Create(Guid.NewGuid().ToString("N"), "canonical-actor", "studio-chat", "verified-session");
    private static AiAdmissionPolicy Generous() => new()
    {
        DeploymentAttempts = 100,
        ActorAttempts = 100,
        CohortAttempts = 100,
        WorkflowAttempts = 100,
        DeploymentConcurrency = 100,
        ActorConcurrency = 100,
        CohortConcurrency = 100,
        WorkflowConcurrency = 100
    };

    private static AiAdmissionControlCommand Control(AiAdmissionState state, AiAdmissionControlAction action) => new(state.DeploymentId,
        action, state.Generation, state.Revision, state.Epoch, "trusted-admin", Guid.NewGuid().ToString("N"), "correlation");

    private static async Task<AiAdmissionState> ReadyAsync(ConnectionMultiplexer connection, RedisAiExecutionAdmissionStore store,
        string deployment, AiAdmissionPolicy policy)
    {
        var initialized = await store.ApplyControlAsync(new(deployment, AiAdmissionControlAction.Initialize,
            Guid.Empty, 0, 0, "trusted-admin", Guid.NewGuid().ToString("N"), "correlation", policy), default);
        initialized.Applied.Should().BeTrue();
        // Test-only elapsed-quarantine simulation. Production has no bypass or startup initialization.
        var key = RedisAiExecutionAdmissionStore.LedgerKey(deployment);
        var state = JsonNode.Parse((string)(await connection.GetDatabase().HashGetAsync(key, "state"))!)!.AsObject();
        state["notBefore"] = 0;
        await connection.GetDatabase().HashSetAsync(key, "state", state.ToJsonString());
        var current = (await store.GetStateAsync(deployment, default))!;
        return (await store.ApplyControlAsync(Control(current, AiAdmissionControlAction.Resume), default)).State!;
    }
}
