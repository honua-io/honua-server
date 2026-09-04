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

    [Fact]
    public async Task WorkflowStore_QueryAsync_IncludesActiveAndTerminalOperationsNewestFirst()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var targetId = $"query-target-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var activeOperation = CreateDeployOperationRecord(
            $"deploy-active-{Guid.NewGuid():N}", targetId, now, WorkflowOperationStatus.Submitted);
        var terminalOperation = CreateDeployOperationRecord(
            $"deploy-terminal-{Guid.NewGuid():N}", targetId, now.AddMinutes(-2), WorkflowOperationStatus.Submitted);

        (await store.TryCreateAsync(activeOperation)).Should().BeTrue();
        (await store.TryCreateAsync(terminalOperation)).Should().BeTrue();
        await store.SetAsync(terminalOperation with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now,
            CompletedAt = now
        });

        var page = await store.QueryAsync(new WorkflowOperationQuery
        {
            Kind = WorkflowOperationKind.Deploy,
            Page = 1,
            PageSize = 200
        });

        var ids = page.Items.Select(item => item.OperationId).ToList();
        ids.Should().Contain(activeOperation.OperationId);
        ids.Should().Contain(terminalOperation.OperationId);

        // Newest-created (active) must sort ahead of the older terminal operation.
        ids.IndexOf(activeOperation.OperationId).Should().BeLessThan(ids.IndexOf(terminalOperation.OperationId));

        // Status filter narrows to the terminal operation and excludes the still-active one.
        var succeededPage = await store.QueryAsync(new WorkflowOperationQuery
        {
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Succeeded,
            Page = 1,
            PageSize = 200
        });
        var succeededIds = succeededPage.Items.Select(item => item.OperationId).ToList();
        succeededIds.Should().Contain(terminalOperation.OperationId);
        succeededIds.Should().NotContain(activeOperation.OperationId);
    }

    [Fact]
    public async Task WorkflowStore_GetMostRecentSucceededDeployByTarget_ReturnsLatestAndPrunesRolledBack()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var targetId = $"succeeded-target-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        (await store.GetMostRecentSucceededDeployByTargetAsync(targetId)).Should().BeNull();

        var older = CreateDeployOperationRecord(
            $"deploy-old-{Guid.NewGuid():N}", targetId, now.AddMinutes(-30), WorkflowOperationStatus.Submitted);
        var newer = CreateDeployOperationRecord(
            $"deploy-new-{Guid.NewGuid():N}", targetId, now.AddMinutes(-10), WorkflowOperationStatus.Submitted);

        (await store.TryCreateAsync(older)).Should().BeTrue();
        (await store.TryCreateAsync(newer)).Should().BeTrue();

        await store.SetAsync(older with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now.AddMinutes(-25),
            CompletedAt = now.AddMinutes(-25),
            Deploy = older.Deploy! with { CurrentRevision = older.Deploy!.DesiredRevision }
        });
        await store.SetAsync(newer with
        {
            Status = WorkflowOperationStatus.Succeeded,
            UpdatedAt = now.AddMinutes(-5),
            CompletedAt = now.AddMinutes(-5)
        });

        var latest = await store.GetMostRecentSucceededDeployByTargetAsync(targetId);
        latest.Should().NotBeNull();
        latest!.OperationId.Should().Be(newer.OperationId);

        // Rolling the latest back demotes it; the prior succeeded deploy becomes the landed revision.
        await store.SetAsync(newer with
        {
            Status = WorkflowOperationStatus.RolledBack,
            UpdatedAt = now,
            CompletedAt = now
        });

        var afterRollback = await store.GetMostRecentSucceededDeployByTargetAsync(targetId);
        afterRollback.Should().NotBeNull();
        afterRollback!.OperationId.Should().Be(older.OperationId);
    }

    private static WorkflowOperationRecord CreateDeployOperationRecord(
        string operationId,
        string targetId,
        DateTimeOffset createdAt,
        WorkflowOperationStatus status)
        => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CurrentPhase = "Test operation.",
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                DesiredRevision = "sha256:" + operationId,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        };

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
