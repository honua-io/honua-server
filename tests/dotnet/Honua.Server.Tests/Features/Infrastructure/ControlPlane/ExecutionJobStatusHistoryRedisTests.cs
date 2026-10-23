// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ExecutionJobStatusHistoryRedisTests(RedisFixture redis) : IAsyncLifetime
{
    private readonly List<string> _ownedJobs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        foreach (var id in _ownedJobs)
        {
            await database.KeyDeleteAsync(["controlplane:job:" + id, "controlplane:job:lease:" + id]);
            await database.SetRemoveAsync("controlplane:job:active", id);
            await database.SetRemoveAsync("controlplane:job:active:geoprocessing", id);
        }
    }

    [IntegrationTest]
    public async Task CreateCasAndOwnedWrites_RetainOnlyActualTransitionsAcrossRestart()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var original = Job(DateTimeOffset.UtcNow) with { StatusTransitions = [Forged()], StatusTransitionsTruncated = true };
        (await store.TryCreateAsync(original)).Should().BeTrue();
        var queued = (await store.GetAsync(original.OperationId))!;
        queued.StatusTransitions.Should().ContainSingle().Which.ToStatus.Should().Be(ExecutionJobStatus.Queued);
        queued.StatusTransitions[0].FromStatus.Should().BeNull();
        queued.StatusTransitionsTruncated.Should().BeFalse();
        (await store.TrySetAsync(queued with { PercentComplete = 1, StatusTransitions = [Forged()] })).Should().BeTrue();
        (await store.TrySetAsync(queued with { Status = ExecutionJobStatus.Cancelled })).Should().BeFalse();
        var latest = (await store.GetAsync(original.OperationId))!;
        (await store.TrySetAsync(latest with { Status = ExecutionJobStatus.Running, AttemptCount = 1 })).Should().BeTrue();
        var running = (await store.GetAsync(original.OperationId))!;
        (await store.TryAcquireLeaseAsync(original.OperationId, "owner", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.TrySetIfLeaseOwnedAsync(running with { Status = ExecutionJobStatus.Succeeded }, original.OperationId, "foreign")).Should().BeFalse();
        (await store.TrySetIfLeaseOwnedAsync(running with { Status = ExecutionJobStatus.Succeeded, StatusTransitions = [] }, original.OperationId, "owner")).Should().BeTrue();

        await using var replacement = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var persisted = (await Store(replacement).GetAsync(original.OperationId))!;
        persisted.StatusTransitions.Select(entry => entry.ToStatus).Should().Equal(ExecutionJobStatus.Queued, ExecutionJobStatus.Running, ExecutionJobStatus.Succeeded);
        persisted.StatusTransitions.Select(entry => entry.Version).Should().Equal(1L, 3L, 4L);
        persisted.StatusTransitions.Select(entry => entry.AttemptCount).Should().Equal(0, 1, 1);
        persisted.StatusTransitions.Should().OnlyContain(entry => entry.JobId == original.OperationId);
        persisted.StatusTransitions[^1].OccurredAt.Should().BeOnOrAfter(persisted.StatusTransitions[0].OccurredAt)
            .And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
        original.StatusTransitions.Should().ContainSingle().Which.JobId.Should().Be("forged");
    }

    [IntegrationTest]
    public async Task BoundedHistory_RetainsActualSuffixAndExplicitlyMarksIncompleteLifecycle()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var original = Job(DateTimeOffset.UtcNow);
        (await store.TryCreateAsync(original)).Should().BeTrue();
        for (var index = 0; index < ExecutionJobStatusHistory.MaximumEntries + 2; index++)
        {
            var current = (await store.GetAsync(original.OperationId))!;
            (await store.TrySetAsync(current with
            {
                Status = index % 2 == 0 ? ExecutionJobStatus.Running : ExecutionJobStatus.Provisioning,
                AttemptCount = 1,
                StatusTransitionsTruncated = false
            })).Should().BeTrue();
        }
        var retained = (await store.GetAsync(original.OperationId))!;
        retained.StatusTransitionsTruncated.Should().BeTrue();
        retained.StatusTransitions.Should().HaveCount(ExecutionJobStatusHistory.MaximumEntries);
        retained.StatusTransitions[0].Version.Should().Be(4);
        retained.StatusTransitions[0].FromStatus.Should().Be(ExecutionJobStatus.Provisioning);
        retained.StatusTransitions[^1].Version.Should().Be(retained.Version);
        retained.StatusTransitions[^1].ToStatus.Should().Be(retained.Status);
        (await store.TrySetAsync(retained with { PercentComplete = 99, StatusTransitionsTruncated = false })).Should().BeTrue();
        (await store.GetAsync(original.OperationId))!.StatusTransitionsTruncated.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task GuardedUnconditionalReplacement_DerivesHistoryFromCurrentRow()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var original = Job(DateTimeOffset.UtcNow);
        (await store.TryCreateAsync(original)).Should().BeTrue();
        var stale = (await store.GetAsync(original.OperationId))!;
        (await store.TrySetAsync(stale with { Status = ExecutionJobStatus.Running, AttemptCount = 1 })).Should().BeTrue();
        await store.SetAsync(stale with { Status = ExecutionJobStatus.Succeeded, AttemptCount = 1, StatusTransitions = [Forged()] });
        var actual = (await store.GetAsync(original.OperationId))!;
        actual.Version.Should().Be(3);
        actual.StatusTransitions.Select(entry => entry.ToStatus).Should().Equal(ExecutionJobStatus.Queued, ExecutionJobStatus.Running, ExecutionJobStatus.Succeeded);
        actual.StatusTransitions[^1].FromStatus.Should().Be(ExecutionJobStatus.Running);
    }

    [IntegrationTest]
    public async Task StorageClockAndNumericIntent_CannotBeReplacedByCallerEvidence()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var original = Job(DateTimeOffset.UtcNow.AddYears(2)) with
        {
            AdmissionCostWeight = 1.2345678901234567,
            StatusTransitions = [Forged()]
        };
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        (await store.TryCreateAsync(original)).Should().BeTrue();

        var stored = (await store.GetAsync(original.OperationId))!;
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        stored.StatusTransitions[0].OccurredAtUnixMilliseconds.Should().BeInRange(before, after);
        stored.StatusTransitions[0].OccurredAt.Should().BeBefore(original.CreatedAt);
        stored.AdmissionCostWeight.Should().Be(original.AdmissionCostWeight);
        Honua.Core.Features.Authorization.Domain.JobExecutionLeaseIntentFingerprint.Compute(stored).Should().Be(
            Honua.Core.Features.Authorization.Domain.JobExecutionLeaseIntentFingerprint.Compute(original));
    }

    [IntegrationTest]
    public async Task ConcurrentCasWriters_PersistOnlyTheWinningActualTransition()
    {
        await using var first = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var second = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var a = Store(first);
        var b = Store(second);
        var original = Job(DateTimeOffset.UtcNow);
        (await a.TryCreateAsync(original)).Should().BeTrue();
        var queued = (await a.GetAsync(original.OperationId))!;

        var results = await Task.WhenAll(
            a.TrySetAsync(queued with { Status = ExecutionJobStatus.Running, AttemptCount = 1 }),
            b.TrySetAsync(queued with { Status = ExecutionJobStatus.Cancelled }));

        results.Count(accepted => accepted).Should().Be(1);
        var actual = (await b.GetAsync(original.OperationId))!;
        actual.StatusTransitions.Should().HaveCount(2);
        actual.StatusTransitions[^1].ToStatus.Should().Be(actual.Status);
        actual.StatusTransitions[^1].Version.Should().Be(2);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReply_NeverAcknowledgesOrInventsAnUnstoredTransition(bool actualCommit)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var real = Store(connection);
        var original = Job(DateTimeOffset.UtcNow);
        (await real.TryCreateAsync(original)).Should().BeTrue();
        var queued = (await real.GetAsync(original.OperationId))!;
        var database = connection.GetDatabase();
        var proxy = Substitute.For<IDatabase>();
        proxy.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => database.StringGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<CommandFlags>(1)));
        proxy.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisResult>>(async call =>
            {
                if (actualCommit) await database.ScriptEvaluateAsync(call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2), call.ArgAt<CommandFlags>(3));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Actual state write reply unavailable");
            });
        var uncertainConnection = Substitute.For<IConnectionMultiplexer>();
        uncertainConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(proxy);
        var uncertain = Store(uncertainConnection);

        await Assert.ThrowsAsync<RedisConnectionException>(() => uncertain.TrySetAsync(queued with { Status = ExecutionJobStatus.Running, AttemptCount = 1 }));

        var actual = (await real.GetAsync(original.OperationId))!;
        actual.Status.Should().Be(actualCommit ? ExecutionJobStatus.Running : ExecutionJobStatus.Queued);
        actual.StatusTransitions.Should().HaveCount(actualCommit ? 2 : 1);
        if (actualCommit)
            (await real.TrySetAsync(queued with { Status = ExecutionJobStatus.Running, AttemptCount = 1 })).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("job")]
    [InlineData("order")]
    [InlineData("version")]
    [InlineData("attempt")]
    [InlineData("time")]
    [InlineData("null")]
    public async Task MalformedRetainedHistory_IsRefusedWithoutBackfill(string mutation)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var original = Job(DateTimeOffset.UtcNow);
        (await store.TryCreateAsync(original)).Should().BeTrue();
        var queued = (await store.GetAsync(original.OperationId))!;
        (await store.TrySetAsync(queued with { Status = ExecutionJobStatus.Running, AttemptCount = 1 })).Should().BeTrue();
        var actual = (await store.GetAsync(original.OperationId))!;
        var entries = actual.StatusTransitions.ToArray();
        if (mutation == "job") entries[1] = entries[1] with { JobId = "foreign" };
        if (mutation == "order") Array.Reverse(entries);
        if (mutation == "version") entries[1] = entries[1] with { Version = entries[0].Version };
        if (mutation == "attempt") entries[1] = entries[1] with { AttemptCount = -1 };
        if (mutation == "time") entries[1] = entries[1] with { OccurredAtUnixMilliseconds = entries[0].OccurredAtUnixMilliseconds - 1000 };
        // Deliberate storage corruption, outside the application's write contract.
        var payload = JsonSerializer.Serialize(actual with { StatusTransitions = mutation == "null" ? null! : entries }, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        if (mutation == "null")
        {
            // The serializer may omit null values; explicit null must differ from
            // an old record which never had this evidence field.
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
            node["statusTransitions"] = null;
            payload = node.ToJsonString();
        }
        var key = "controlplane:job:" + original.OperationId;
        await connection.GetDatabase().StringSetAsync(key, payload);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(original.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(actual));
        (await connection.GetDatabase().StringGetAsync(key)).ToString().Should().Be(payload);
    }

    [IntegrationTest]
    public async Task LegacyRows_RemainEmptyAndDoNotAcquireSyntheticHistory()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var legacy = Job(DateTimeOffset.UtcNow) with { Version = 7, Status = ExecutionJobStatus.Running, AttemptCount = 1 };
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(legacy, ControlPlaneJsonContext.Default.ExecutionJobRecord))!.AsObject();
        legacyJson.Remove("statusTransitions");
        await connection.GetDatabase().StringSetAsync("controlplane:job:" + legacy.OperationId, legacyJson.ToJsonString());

        (await store.TrySetAsync(legacy with { Status = ExecutionJobStatus.Succeeded, StatusTransitions = [Forged()] })).Should().BeTrue();

        (await store.GetAsync(legacy.OperationId))!.StatusTransitions.Should().BeEmpty();
    }

    private static RedisExecutionJobStore Store(IConnectionMultiplexer connection)
        => new(connection, NullLogger<RedisExecutionJobStore>.Instance);

    private ExecutionJobRecord Job(DateTimeOffset now)
    {
        var id = Guid.NewGuid().ToString("N");
        _ownedJobs.Add(id);
        return new()
        {
            OperationId = id, Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            Spec = new() { Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob, Backend = "local", WorkloadName = "history-control" }
        };
    }

    private static ExecutionJobStatusTransition Forged() => new()
    {
        JobId = "forged", Version = 9000, AttemptCount = 100, OccurredAtUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), ToStatus = ExecutionJobStatus.Succeeded
    };
}
