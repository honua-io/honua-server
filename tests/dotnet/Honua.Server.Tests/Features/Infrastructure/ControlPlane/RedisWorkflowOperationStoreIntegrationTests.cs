// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
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

    [Fact]
    public async Task WorkflowStore_MetadataReleasePackageLookup_ReturnsLatestOperationAndKeepsPriorOperationById()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = multiplexer.GetDatabase();
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var first = CreateMetadataReleaseOperationRecord(packageId, "metadata-op-a");
        var second = CreateMetadataReleaseOperationRecord(packageId, "metadata-op-b");
        var retention = TimeSpan.FromHours(6);

        (await store.TryCreateAsync(first, retention)).Should().BeTrue();
        (await store.TryCreateAsync(second, retention)).Should().BeTrue();

        var byPackage = await store.GetByMetadataPackageIdAsync(packageId);
        byPackage.Should().NotBeNull();
        byPackage!.OperationId.Should().Be(second.OperationId);

        var byFirstOperationId = await store.GetAsync(first.OperationId);
        byFirstOperationId.Should().NotBeNull();
        byFirstOperationId!.MetadataRelease!.PackageId.Should().Be(packageId);

        var operationTtl = await database.KeyTimeToLiveAsync($"controlplane:workflow:{second.OperationId}");
        var indexTtl = await database.KeyTimeToLiveAsync($"controlplane:workflow:metapkg:{packageId}");
        operationTtl.Should().NotBeNull();
        indexTtl.Should().NotBeNull();
        Math.Abs((operationTtl!.Value - indexTtl!.Value).TotalSeconds).Should().BeLessThan(5);
    }

    [Fact]
    public async Task WorkflowStore_MetadataReleasePackageLookup_OlderRetryUpdate_DoesNotReplaceLatestIndex()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = multiplexer.GetDatabase();
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var first = CreateMetadataReleaseOperationRecord(packageId, "metadata-op-a");
        var second = CreateMetadataReleaseOperationRecord(packageId, "metadata-op-b");
        var retention = TimeSpan.FromHours(6);

        (await store.TryCreateAsync(first, retention)).Should().BeTrue();
        (await store.TryCreateAsync(second, retention)).Should().BeTrue();

        var firstFailed = first with
        {
            Status = WorkflowOperationStatus.Failed,
            UpdatedAt = first.UpdatedAt.AddMinutes(10),
            CompletedAt = first.UpdatedAt.AddMinutes(10),
            CurrentPhase = "Older retry failed after a newer retry was created.",
            MetadataRelease = first.MetadataRelease! with
            {
                CurrentStage = MetadataReleaseStage.Failed
            }
        };
        await store.SetAsync(firstFailed, retention);

        var byPackage = await store.GetByMetadataPackageIdAsync(packageId);
        byPackage.Should().NotBeNull();
        byPackage!.OperationId.Should().Be(second.OperationId);

        var byFirstOperationId = await store.GetAsync(first.OperationId);
        byFirstOperationId.Should().NotBeNull();
        byFirstOperationId!.Status.Should().Be(WorkflowOperationStatus.Failed);

        var refreshRetention = TimeSpan.FromHours(12);
        var secondUpdated = second with
        {
            UpdatedAt = second.UpdatedAt.AddMinutes(15),
            CurrentPhase = "Latest retry moved to smoke verification.",
            MetadataRelease = second.MetadataRelease! with
            {
                CurrentStage = MetadataReleaseStage.Smoke
            }
        };
        await store.SetAsync(secondUpdated, refreshRetention);

        var refreshedByPackage = await store.GetByMetadataPackageIdAsync(packageId);
        refreshedByPackage.Should().NotBeNull();
        refreshedByPackage!.OperationId.Should().Be(second.OperationId);

        var refreshedOperationTtl = await database.KeyTimeToLiveAsync($"controlplane:workflow:{second.OperationId}");
        var refreshedIndexTtl = await database.KeyTimeToLiveAsync($"controlplane:workflow:metapkg:{packageId}");
        refreshedOperationTtl.Should().NotBeNull();
        refreshedIndexTtl.Should().NotBeNull();
        Math.Abs((refreshedOperationTtl!.Value - refreshedIndexTtl!.Value).TotalSeconds).Should().BeLessThan(5);
    }

    [Fact]
    public async Task WorkflowStore_MetadataReleasePackageLookup_WithStaleIndex_ReturnsNull()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = multiplexer.GetDatabase();
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var packageId = $"metadata-package-{Guid.NewGuid():N}";

        await database.StringSetAsync(
            $"controlplane:workflow:metapkg:{packageId}",
            $"metadata-op-missing-{Guid.NewGuid():N}",
            TimeSpan.FromMinutes(10));

        var result = await store.GetByMetadataPackageIdAsync(packageId);
        result.Should().BeNull();
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

    private static WorkflowOperationRecord CreateMetadataReleaseOperationRecord(string packageId, string operationPrefix)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowOperationRecord
        {
            OperationId = $"{operationPrefix}-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Metadata release preflight complete.",
            Warnings = ["non-blocking-field-diff"],
            BlockingReasons = ["smoke-pending"],
            MetadataRelease = new MetadataReleaseContext
            {
                PackageId = packageId,
                GitOperationId = "git-write-42",
                PrUrl = "https://github.com/honua-io/honua-server/pull/1165",
                CommitSha = "abcdef1234567890",
                DesiredRevision = "refs/tags/metadata-release-v1",
                TargetEnvironment = "staging",
                DeployOperationId = "deploy-linked-1",
                JobIds = ["job-backup-1", "job-smoke-1"],
                EvidenceRefs =
                [
                    new MetadataEvidenceRef
                    {
                        Kind = "compatibility-prevalidation",
                        RefId = "evidence-1",
                        Uri = "honua://evidence/evidence-1",
                        At = now
                    }
                ],
                CurrentStage = MetadataReleaseStage.Preflight,
                RollbackPlan = new MetadataRollbackPlan
                {
                    Class = MetadataRollbackClass.MetadataOnly,
                    RequiresExplicitApproval = false,
                    Steps = ["Revert metadata package commit."],
                    EvidenceRequired = []
                },
                Warnings = ["target-has-extra-field"],
                Blockers = ["awaiting-smoke"]
            }
        };
    }
}
