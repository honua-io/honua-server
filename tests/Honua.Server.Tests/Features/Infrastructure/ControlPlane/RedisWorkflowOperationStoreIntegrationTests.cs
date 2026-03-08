// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis integration tests for durable workflow operation storage and leases.
/// </summary>
[Collection("Redis")]
public sealed class RedisWorkflowOperationStoreIntegrationTests(RedisFixture redis)
{
    [Fact]
    public async Task WorkflowStore_WithRedis_RoundTripsAndMaintainsActiveIndex()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var operation = CreateOperationRecord();

        var created = await store.TryCreateAsync(operation);
        created.Should().BeTrue();

        var loaded = await store.GetAsync(operation.OperationId);
        loaded.Should().NotBeNull();
        loaded!.Deploy!.TargetId.Should().Be(operation.Deploy!.TargetId);

        var active = await store.ListActiveAsync(WorkflowOperationKind.Deploy);
        active.Should().ContainSingle(entry => entry.OperationId == operation.OperationId);

        await store.SetAsync(operation with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });

        var activeAfterCompletion = await store.ListActiveAsync(WorkflowOperationKind.Deploy);
        activeAfterCompletion.Should().NotContain(entry => entry.OperationId == operation.OperationId);
    }

    [Fact]
    public async Task WorkflowStore_Leases_AreExclusivePerOperation()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var operationId = $"lease-{Guid.NewGuid():N}";

        var firstLease = await store.TryAcquireLeaseAsync(operationId, "worker-a", TimeSpan.FromSeconds(30));
        var secondLease = await store.TryAcquireLeaseAsync(operationId, "worker-b", TimeSpan.FromSeconds(30));
        var renewed = await store.RenewLeaseAsync(operationId, "worker-a", TimeSpan.FromSeconds(30));

        await store.ReleaseLeaseAsync(operationId, "worker-a");
        var thirdLease = await store.TryAcquireLeaseAsync(operationId, "worker-b", TimeSpan.FromSeconds(30));

        firstLease.Should().BeTrue();
        secondLease.Should().BeFalse();
        renewed.Should().BeTrue();
        thirdLease.Should().BeTrue();
    }

    private static WorkflowOperationRecord CreateOperationRecord()
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
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
}
