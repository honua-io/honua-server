// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Unit")]
public sealed class RedisWorkflowOperationStoreAtomicityTests
{
    [UnitTest]
    public async Task TryCreateAsync_WhenActiveIndexWriteFails_DoesNotLeavePartiallyPersistedOperation()
    {
        var harness = new WorkflowRedisHarness(throwOnDirectSetAdd: true);
        var store = new RedisWorkflowOperationStore(harness.Redis, NullLogger<RedisWorkflowOperationStore>.Instance);
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);

        await FluentActions
            .Invoking(() => store.TryCreateAsync(operation))
            .Should()
            .ThrowAsync<RedisConnectionException>();

        (await store.GetAsync(operation.OperationId)).Should().BeNull();
        (await store.ListActiveAsync(WorkflowOperationKind.Deploy)).Should().BeEmpty();
    }

    [UnitTest]
    public async Task SetAsync_WhenTerminalUpdateFails_DoesNotReplaceTheActiveOperationState()
    {
        var harness = new WorkflowRedisHarness(throwOnDirectSetRemove: true);
        var store = new RedisWorkflowOperationStore(harness.Redis, NullLogger<RedisWorkflowOperationStore>.Instance);
        var original = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        var terminal = original with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = original.UpdatedAt.AddMinutes(1),
            CompletedAt = original.UpdatedAt.AddMinutes(1)
        };

        harness.SeedOperation(original);

        await FluentActions
            .Invoking(() => store.SetAsync(terminal))
            .Should()
            .ThrowAsync<RedisConnectionException>();

        var loaded = await store.GetAsync(original.OperationId);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(WorkflowOperationStatus.Submitted);

        var active = await store.ListActiveAsync(WorkflowOperationKind.Deploy);
        active.Should().ContainSingle(entry => entry.OperationId == original.OperationId);
    }

    private static WorkflowOperationRecord CreateOperationRecord(WorkflowOperationStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted to deploy backend.",
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                DesiredRevision = "sha256:test",
                Parameters = new Dictionary<string, string>
                {
                    ["namespace"] = "honua"
                }
            }
        };
    }

    private sealed class WorkflowRedisHarness
    {
        private const string ActiveOperationsKey = "controlplane:workflow:active";
        private readonly ConcurrentDictionary<string, RedisValue> _stringValues = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _setValues = new(StringComparer.Ordinal);
        private readonly bool _throwOnDirectSetAdd;
        private readonly bool _throwOnDirectSetRemove;

        public WorkflowRedisHarness(bool throwOnDirectSetAdd = false, bool throwOnDirectSetRemove = false)
        {
            _throwOnDirectSetAdd = throwOnDirectSetAdd;
            _throwOnDirectSetRemove = throwOnDirectSetRemove;

            var database = Substitute.For<IDatabase>();
            var transaction = Substitute.For<ITransaction>();
            var redis = Substitute.For<IConnectionMultiplexer>();

            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

            database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    _stringValues[key] = call.ArgAt<RedisValue>(1);
                    return Task.FromResult(true);
                });

            database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    return Task.FromResult(_stringValues.TryGetValue(key, out var value) ? value : RedisValue.Null);
                });

            database.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    if (_throwOnDirectSetAdd)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated active-index failure");
                    }

                    AddSetMember(call.ArgAt<RedisKey>(0).ToString(), call.ArgAt<RedisValue>(1).ToString());
                    return Task.FromResult(true);
                });

            database.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    if (_throwOnDirectSetRemove)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated active-index failure");
                    }

                    RemoveSetMember(call.ArgAt<RedisKey>(0).ToString(), call.ArgAt<RedisValue>(1).ToString());
                    return Task.FromResult(true);
                });

            database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var key = call.ArgAt<RedisKey>(0).ToString();
                    return Task.FromResult(GetSetMembers(key));
                });

            database.CreateTransaction().Returns(transaction);

            transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(true));
            transaction.ExecuteAsync(Arg.Any<CommandFlags>())
                .Returns(Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated transaction failure")));

            Redis = redis;
        }

        public IConnectionMultiplexer Redis { get; }

        public void SeedOperation(WorkflowOperationRecord operation)
        {
            var key = $"controlplane:workflow:{operation.OperationId}";
            _stringValues[key] = JsonSerializer.Serialize(operation, ControlPlaneJsonContext.Default.WorkflowOperationRecord);

            if (operation.Status is not WorkflowOperationStatus.Succeeded
                and not WorkflowOperationStatus.Failed
                and not WorkflowOperationStatus.RolledBack
                and not WorkflowOperationStatus.ManualInterventionRequired)
            {
                AddSetMember(ActiveOperationsKey, operation.OperationId);
                AddSetMember(GetKindActiveKey(operation.Kind), operation.OperationId);
            }
        }

        private void AddSetMember(string key, string member)
        {
            var set = _setValues.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            set[member] = 1;
        }

        private void RemoveSetMember(string key, string member)
        {
            if (_setValues.TryGetValue(key, out var set))
            {
                set.TryRemove(member, out _);
            }
        }

        private RedisValue[] GetSetMembers(string key)
        {
            if (!_setValues.TryGetValue(key, out var set))
            {
                return [];
            }

            return set.Keys.Select(member => (RedisValue)member).ToArray();
        }

        private static string GetKindActiveKey(WorkflowOperationKind kind)
            => $"controlplane:workflow:active:{kind.ToString().ToLowerInvariant()}";
    }
}
