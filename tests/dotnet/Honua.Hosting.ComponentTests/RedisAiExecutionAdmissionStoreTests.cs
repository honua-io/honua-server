// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using StackExchange.Redis;

namespace Honua.Hosting.ComponentTests;

/// <summary>Real Redis proofs for atomic shared admission, fencing and explicit stopped recovery.</summary>
[Collection(AiAdmissionRedisFixture.CollectionName)]
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisAiExecutionAdmissionStoreTests(AiAdmissionRedisFixture fixture)
{
    [IntegrationTest]
    public async Task ApplyControlAsync_MissingLedger_RequiresExplicitStoppedBootstrapAndQuarantine()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("uninitialized");
        (await store.GetStateAsync(context.DeploymentId, default)).Should().BeNull();

        var before = await ServerTimeAsync(connection);
        var initialized = await store.ApplyControlAsync(Initialize(context.DeploymentId), default);
        var after = await ServerTimeAsync(connection);
        initialized.Applied.Should().BeTrue();
        initialized.State!.Stopped.Should().BeTrue();
        initialized.State.NotBeforeUtc.Should().BeOnOrAfter(before.AddSeconds(3630));
        initialized.State.NotBeforeUtc.Should().BeOnOrBefore(after.AddSeconds(3630));
        (await store.ApplyControlAsync(Control(initialized.State, AiAdmissionControlAction.Resume), default)).Code.Should().Be("recovering");
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Granted.Should().BeFalse();
        (await store.ApplyControlAsync(Initialize(context.DeploymentId), default)).Code.Should().Be("already_initialized");
        (await connection.GetDatabase().KeyTimeToLiveAsync(RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId))).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_TwoIndependentAuthorities_RaceEveryScopeWithoutOversubscription()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        var stores = new[] { new RedisAiExecutionAdmissionStore(first), new RedisAiExecutionAdmissionStore(second) };
        foreach (var scope in new[] { "deployment", "actor", "cohort", "workflow" })
        {
            var policy = Unlimited() with
            {
                DeploymentAttempts = scope == "deployment" ? 1 : 100,
                ActorAttempts = scope == "actor" ? 1 : 100,
                CohortAttempts = scope == "cohort" ? 1 : 100,
                WorkflowAttempts = scope == "workflow" ? 1 : 100
            };
            var context = Context();
            await ReadyAsync(first, stores[0], context.DeploymentId, policy);
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 24).Select(async i =>
            {
                await barrier.Task;
                return await stores[i % 2].TryAcquireAsync(context, Guid.NewGuid(), default);
            }).ToArray();
            barrier.SetResult();
            var results = await Task.WhenAll(requests);
            results.Count(result => result.Granted).Should().Be(1, scope);
            results.Count(result => result.Code == "budget_exceeded").Should().Be(23, scope);
        }
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_DeniedInnerScope_DoesNotPartiallyChargeDeploymentOrWorkflow()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var first = Context();
        await ReadyAsync(connection, store, first.DeploymentId, Unlimited() with { DeploymentAttempts = 2, ActorAttempts = 1, WorkflowAttempts = 1 });
        (await store.TryAcquireAsync(first, Guid.NewGuid(), default)).Granted.Should().BeTrue();
        var denied = first with { WorkflowKey = new string('b', 64) };
        (await store.TryAcquireAsync(denied, Guid.NewGuid(), default)).Code.Should().Be("budget_exceeded");
        var next = denied with { ActorKey = new string('c', 64) };
        (await store.TryAcquireAsync(next, Guid.NewGuid(), default)).Granted.Should().BeTrue();
        (await connection.GetDatabase().HashLengthAsync(RedisAiExecutionAdmissionStore.LedgerKey(first.DeploymentId))).Should().Be(5);
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_WorkflowLimit_AggregatesAcrossCanonicalActors()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        await ReadyAsync(connection, store, context.DeploymentId, Unlimited() with { WorkflowConcurrency = 1 });
        var acquired = await store.TryAcquireAsync(context, Guid.NewGuid(), default);
        var otherActor = context with { ActorKey = new string('f', 64) };
        (await store.TryAcquireAsync(otherActor, Guid.NewGuid(), default)).Code.Should().Be("concurrency_exceeded");
        await store.ReleaseAsync(acquired.Reservation!, default);
        (await store.TryAcquireAsync(otherActor, Guid.NewGuid(), default)).Granted.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ReleaseAsync_RepeatedAndStaleReleases_NeverFreeAnotherReservationOrRefundCharges()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        await ReadyAsync(connection, store, context.DeploymentId, Unlimited() with { DeploymentConcurrency = 1, DeploymentAttempts = 2 });
        var first = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        await store.ReleaseAsync(first, default);
        await store.ReleaseAsync(first, default);
        var second = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        await store.ReleaseAsync(first, default);
        await store.ReleaseAsync(second with { Generation = Guid.NewGuid() }, default);
        (await store.IsCurrentAsync(second, default)).Should().BeTrue();
        await store.ReleaseAsync(second, default);
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("budget_exceeded");
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_StopResumeAndReplay_FenceActivePermitsWithoutResettingCounters()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        var one = new RedisAiExecutionAdmissionStore(first);
        var two = new RedisAiExecutionAdmissionStore(second);
        var context = Context();
        var ready = await ReadyAsync(first, one, context.DeploymentId, Unlimited() with { DeploymentAttempts = 1 });
        var reservation = (await one.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        var stop = Control(ready, AiAdmissionControlAction.Stop);
        var stopped = await two.ApplyControlAsync(stop, default);
        stopped.Applied.Should().BeTrue();
        (await one.IsCurrentAsync(reservation, default)).Should().BeFalse();
        (await two.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("stopped");
        (await two.ApplyControlAsync(stop, default)).Should().BeEquivalentTo(stopped);
        (await two.ApplyControlAsync(stop with { CorrelationId = "changed" }, default)).Code.Should().Be("invalid_control");
        var resumed = await one.ApplyControlAsync(Control(stopped.State!, AiAdmissionControlAction.Resume), default);
        resumed.Applied.Should().BeTrue();
        (await one.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("budget_exceeded");
        (await two.ApplyControlAsync(stop with { InvocationId = Guid.NewGuid().ToString("N") }, default)).Code.Should().Be("stale_control");
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_LongerPolicyWindow_CountsEarlierRevisionHistory()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        var ready = await ReadyAsync(connection, store, context.DeploymentId, Unlimited() with { WindowSeconds = 1, DeploymentAttempts = 1 });
        var reservation = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        await store.ReleaseAsync(reservation, default);
        await Task.Delay(1100);
        var stopped = await store.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Stop), default);
        var updated = await store.ApplyControlAsync(Control(stopped.State!, AiAdmissionControlAction.UpdatePolicy) with
        {
            Policy = Unlimited() with { WindowSeconds = 3600, DeploymentAttempts = 1 }
        }, default);
        updated.Applied.Should().BeTrue();
        await store.ApplyControlAsync(Control(updated.State!, AiAdmissionControlAction.Resume), default);
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("budget_exceeded");
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_LostLedgerAndUnavailableConnection_DenyAndRequireNewGenerationRecovery()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        var ready = await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        var old = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        await connection.GetDatabase().KeyDeleteAsync(RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId));
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("uninitialized");
        (await store.IsCurrentAsync(old, default)).Should().BeFalse();
        var initialized = await store.ApplyControlAsync(Initialize(context.DeploymentId), default);
        initialized.State!.Generation.Should().NotBe(ready.Generation);
        initialized.State.Stopped.Should().BeTrue();
        (await store.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Resume), default)).Code.Should().Be("stale_control");
        (await store.ApplyControlAsync(Control(initialized.State, AiAdmissionControlAction.Resume), default)).Code.Should().Be("recovering");
        using var unavailable = await ConnectAsync();
        var unavailableStore = new RedisAiExecutionAdmissionStore(unavailable);
        unavailable.Dispose();
        (await unavailableStore.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("unavailable");
        (await unavailableStore.IsCurrentAsync(old, default)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_CallerCancelledBeforeDispatch_DoesNotCharge()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var call = () => store.TryAcquireAsync(context, Guid.NewGuid(), cancelled.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
        (await connection.GetDatabase().HashLengthAsync(RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId))).Should().Be(3);
    }

    [IntegrationTest]
    public async Task IsCurrentAsync_HardDeadlineAndDrainExpiry_RecoverAbandonedCapacityWithoutRefundingAttempt()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        await ReadyAsync(connection, store, context.DeploymentId, Unlimited() with { MaxOperationSeconds = 1, DeploymentConcurrency = 1 });
        var original = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        (original.LeaseExpiresUtc - original.DeadlineUtc).Should().Be(TimeSpan.FromSeconds(30));
        await Task.Delay(1100);
        (await store.IsCurrentAsync(original, default)).Should().BeFalse();
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("concurrency_exceeded");

        // Test-only elapsed-drain simulation changes all fixed times together, never a production heartbeat.
        var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
        var field = "a:" + original.AttemptId.ToString("N");
        var record = JsonNode.Parse((string)(await connection.GetDatabase().HashGetAsync(key, field))!)!.AsObject();
        foreach (var name in new[] { "admitted", "deadline", "lease" })
        {
            record[name] = record[name]!.GetValue<long>() - 31_000;
        }
        await connection.GetDatabase().HashSetAsync(key, field, record.ToJsonString());
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Granted.Should().BeTrue();
        (await connection.GetDatabase().HashExistsAsync(key, field)).Should().BeTrue("the charged attempt remains inside supported history");
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_AttemptCapacityAndSafePruning_EnforceBoundWithoutTwentyFourHourRetention()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        var ready = await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = Enumerable.Range(0, AiAdmissionPolicy.MaximumLedgerEntries).Select(_ =>
            new HashEntry("a:" + Guid.NewGuid().ToString("N"), AttemptJson(ready, context, now, true))).ToArray();
        await connection.GetDatabase().HashSetAsync(key, entries);
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("ledger_full");
        var aged = entries.Select(entry => new HashEntry(entry.Name, AttemptJson(ready, context, now - 3_631_000, true))).ToArray();
        await connection.GetDatabase().HashSetAsync(key, aged);
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Granted.Should().BeTrue();
        (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(4, "only policy, two control events and one attempt remain");
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_FullAuditHistory_ReservesStopAndNeverChargesNoopsOrRejections()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        var ready = await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
        var existing = await connection.GetDatabase().HashGetAllAsync(key);
        var sample = JsonNode.Parse((string)existing.First(entry => ((string)entry.Name!).StartsWith("c:", StringComparison.Ordinal)).Value!)!.AsObject();
        var events = new List<HashEntry>();
        for (var i = 0; i < RedisAiExecutionAdmissionStore.MaximumControlEvents - 3; i++)
        {
            var record = sample.DeepClone().AsObject();
            var id = Guid.NewGuid().ToString("N");
            record["invocation"] = id;
            events.Add(new HashEntry("c:" + Digest(id), record.ToJsonString()));
        }
        await connection.GetDatabase().HashSetAsync(key, events.ToArray());
        var before = await connection.GetDatabase().HashLengthAsync(key);
        before.Should().Be(1024, "1023 audits plus policy leaves one reserved STOP slot");
        var noop = await store.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Resume), default);
        noop.Applied.Should().BeFalse();
        (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(before);
        var stopped = await store.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Stop), default);
        stopped.Applied.Should().BeTrue();
        stopped.State!.Stopped.Should().BeTrue();
        (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(1025);
        (await store.ApplyControlAsync(Control(stopped.State, AiAdmissionControlAction.Stop), default)).Applied.Should().BeFalse();
        (await store.ApplyControlAsync(Control(stopped.State, AiAdmissionControlAction.Resume), default)).Code.Should().Be("audit_full");
        (await store.ApplyControlAsync(Control(ready, AiAdmissionControlAction.Stop), default)).Code.Should().Be("stale_control");
        (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(1025);
        (await store.GetStateAsync(context.DeploymentId, default)).Should().BeEquivalentTo(stopped.State);
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_ExpiredAuditPruning_DoesNotReplayAnOldApproval()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        var ready = await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        var stop = Control(ready, AiAdmissionControlAction.Stop);
        var stopped = await store.ApplyControlAsync(stop, default);
        var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
        foreach (var entry in await connection.GetDatabase().HashGetAllAsync(key))
        {
            if (!((string)entry.Name!).StartsWith("c:", StringComparison.Ordinal))
            {
                continue;
            }
            var record = JsonNode.Parse((string)entry.Value!)!.AsObject();
            record["at"] = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
            await connection.GetDatabase().HashSetAsync(key, entry.Name, record.ToJsonString());
        }
        var resumed = await store.ApplyControlAsync(Control(stopped.State!, AiAdmissionControlAction.Resume), default);
        resumed.Applied.Should().BeTrue();
        (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(2, "old audit events are pruned only after retention");
        (await store.ApplyControlAsync(stop, default)).Code.Should().Be("stale_control");
        (await store.GetStateAsync(context.DeploymentId, default)).Should().BeEquivalentTo(resumed.State);
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_CorruptPolicyAttemptOrExpiringAuthority_FailsClosed()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        foreach (var corruption in new[] { "policy", "attempt", "ttl", "audit-field" })
        {
            var context = Context();
            await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
            var reservation = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
            var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
            if (corruption == "policy")
            {
                await ModifyStateAsync(connection, context.DeploymentId, state => state["policy"]!["windowSeconds"] = 0);
            }
            else if (corruption == "attempt")
            {
                await connection.GetDatabase().HashSetAsync(key, "a:" + Guid.NewGuid().ToString("N"), "corrupt-json");
            }
            else if (corruption == "audit-field")
            {
                await connection.GetDatabase().HashSetAsync(key, "c:unbounded-or-invalid-field", "{}");
            }
            else
            {
                await connection.GetDatabase().KeyExpireAsync(key, TimeSpan.FromMinutes(1));
            }
            (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Granted.Should().BeFalse(corruption);
            (await store.IsCurrentAsync(reservation, default)).Should().BeFalse(corruption);
        }
    }

    [IntegrationTest]
    public async Task ApplyControlAsync_UnicodeAndEscapedMetadataBounds_RefuseBeforeWritingUnreadableAudit()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        foreach (var actor in new[] { new string('é', 3000), new string('\0', 4096) })
        {
            var context = Context();
            var command = Initialize(context.DeploymentId) with { ActorId = actor };
            command.IsValid.Should().BeTrue("Core bounds characters while this backend additionally bounds encoded records");
            (await store.ApplyControlAsync(command, default)).Code.Should().Be("invalid_control");
            (await connection.GetDatabase().KeyExistsAsync(RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId))).Should().BeFalse();
        }
        var valid = Context();
        var initialized = await store.ApplyControlAsync(Initialize(valid.DeploymentId) with { ActorId = "operator-é" }, default);
        initialized.Applied.Should().BeTrue();
        (await store.GetStateAsync(valid.DeploymentId, default)).Should().NotBeNull();
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_WeakerPersistenceConfiguration_DeniesWithoutChangingAccounting()
    {
        using var connection = await ConnectAsync();
        var store = new RedisAiExecutionAdmissionStore(connection);
        var context = Context();
        await ReadyAsync(connection, store, context.DeploymentId, Unlimited());
        var reservation = (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Reservation!;
        var server = connection.GetServer(connection.GetEndPoints().Single());
        var key = RedisAiExecutionAdmissionStore.LedgerKey(context.DeploymentId);
        var before = await connection.GetDatabase().HashLengthAsync(key);
        foreach (var (name, weaker, original) in new[]
        {
            ("appendfsync", "everysec", "always"),
            ("no-appendfsync-on-rewrite", "yes", "no"),
            ("maxmemory-policy", "allkeys-lru", "noeviction"),
            ("appendonly", "no", "yes")
        })
        {
            try
            {
                await server.ConfigSetAsync(name, weaker);
                (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("unavailable", name);
                (await store.IsCurrentAsync(reservation, default)).Should().BeFalse(name);
                (await connection.GetDatabase().HashLengthAsync(key)).Should().Be(before);
            }
            finally
            {
                await server.ConfigSetAsync(name, original);
            }
        }
    }

    private static string AttemptJson(AiAdmissionState state, AiExecutionContext context, long admitted, bool released)
        => JsonSerializer.Serialize(new
        {
            generation = state.Generation.ToString("N"), epoch = state.Epoch, actor = context.ActorKey,
            useCase = context.UseCase, workflow = context.WorkflowKey, admitted,
            deadline = admitted + 1000, lease = admitted + 31_000, released
        });

    private static async Task<DateTimeOffset> ServerTimeAsync(ConnectionMultiplexer connection)
    {
        var parts = (RedisResult[])(await connection.GetDatabase().ExecuteAsync("TIME"))!;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)parts[0] * 1000 + (long)parts[1] / 1000);
    }

    private static string Digest(string value)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse(fixture.ConnectionString);
        options.AllowAdmin = true;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
    private static AiExecutionContext Context() => AiExecutionContext.Create(Guid.NewGuid().ToString("N"), "canonical-actor", "studio-chat", "verified-session");
    private static AiAdmissionPolicy Unlimited() => new()
    {
        DeploymentAttempts = 100, ActorAttempts = 100, CohortAttempts = 100, WorkflowAttempts = 100,
        DeploymentConcurrency = 100, ActorConcurrency = 100, CohortConcurrency = 100, WorkflowConcurrency = 100
    };
    private static AiAdmissionControlCommand Initialize(string deployment) => new(deployment, AiAdmissionControlAction.Initialize,
        Guid.Empty, 0, 0, "trusted-admin", Guid.NewGuid().ToString("N"), "correlation", Unlimited());
    private static AiAdmissionControlCommand Control(AiAdmissionState state, AiAdmissionControlAction action) => new(state.DeploymentId,
        action, state.Generation, state.Revision, state.Epoch, "trusted-admin", Guid.NewGuid().ToString("N"), "correlation");

    private static async Task<AiAdmissionState> ReadyAsync(ConnectionMultiplexer connection, RedisAiExecutionAdmissionStore store,
        string deployment, AiAdmissionPolicy policy)
    {
        var initialized = await store.ApplyControlAsync(Initialize(deployment) with { Policy = policy }, default);
        initialized.Applied.Should().BeTrue();
        // Test-only elapsed-quarantine simulation; production never changes or bypasses NotBefore.
        await ModifyStateAsync(connection, deployment, state => state["notBefore"] = 0);
        var state = (await store.GetStateAsync(deployment, default))!;
        return (await store.ApplyControlAsync(Control(state, AiAdmissionControlAction.Resume), default)).State!;
    }

    private static async Task ModifyStateAsync(ConnectionMultiplexer connection, string deployment, Action<JsonObject> change)
    {
        var key = RedisAiExecutionAdmissionStore.LedgerKey(deployment);
        var state = JsonNode.Parse((string)(await connection.GetDatabase().HashGetAsync(key, "state"))!)!.AsObject();
        change(state);
        await connection.GetDatabase().HashSetAsync(key, "state", state.ToJsonString());
    }
}
