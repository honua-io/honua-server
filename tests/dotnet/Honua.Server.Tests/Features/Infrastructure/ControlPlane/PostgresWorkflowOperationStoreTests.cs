// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresWorkflowOperationStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TryCreateAsync_SameIdOnEightStores_ExactlyOneWinsAtVersionOne()
    {
        await using var harness = await CreateHarnessAsync();
        var operation = Deploy(NewTargetId(), DateTimeOffset.UtcNow);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.CreateWorkflowOperationStore().TryCreateAsync(operation)));

        outcomes.Count(created => created).Should().Be(1);
        (await harness.CreateWorkflowOperationStore().GetAsync(operation.OperationId))!.Version.Should().Be(1);
    }

    [IntegrationTest]
    public async Task ListActiveAsync_AfterTheOperationSucceeds_NoLongerListsIt()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var operation = Deploy(NewTargetId(), DateTimeOffset.UtcNow);
        await store.TryCreateAsync(operation);
        (await store.ListActiveAsync(WorkflowOperationKind.Deploy)).Should().ContainSingle(entry => entry.OperationId == operation.OperationId);
        (await store.ListActiveAsync(WorkflowOperationKind.MetadataRelease)).Should().BeEmpty();

        await store.SetAsync(Succeeded(operation, DateTimeOffset.UtcNow));

        (await store.ListActiveAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task TrySetAsync_StaleVersion_IsRefusedAndTheFirstWriteStands()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var operation = Deploy(NewTargetId(), DateTimeOffset.UtcNow) with { Status = WorkflowOperationStatus.Reconciling };
        await store.TryCreateAsync(operation);
        var staleReconcilerRead = await store.GetAsync(operation.OperationId);
        var rollbackRead = await store.GetAsync(operation.OperationId);

        (await store.TrySetAsync(rollbackRead! with { Status = WorkflowOperationStatus.RollbackRequested })).Should().BeTrue();
        (await store.TrySetAsync(Succeeded(staleReconcilerRead!, DateTimeOffset.UtcNow))).Should().BeFalse();

        var stored = await store.GetAsync(operation.OperationId);
        stored!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        stored.Version.Should().Be(2);
        (await store.ListActiveAsync(WorkflowOperationKind.Deploy)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task Leases_AreExclusivePerOperationUntilReleased()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var operationId = $"lease-{Guid.NewGuid():N}";
        var leaseDuration = TimeSpan.FromSeconds(30);

        (await store.TryAcquireLeaseAsync(operationId, "worker-a", leaseDuration)).Should().BeTrue();
        (await store.TryAcquireLeaseAsync(operationId, "worker-b", leaseDuration)).Should().BeFalse();
        (await store.RenewLeaseAsync(operationId, "worker-a", leaseDuration)).Should().BeTrue();
        await store.ReleaseLeaseAsync(operationId, "worker-a");
        (await store.TryAcquireLeaseAsync(operationId, "worker-b", leaseDuration)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task GetByMetadataPackageIdAsync_ReturnsTheLastCreatedOperationEvenAfterAnOlderOneIsUpdated()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var first = MetadataRelease(packageId);
        var second = MetadataRelease(packageId);
        await store.TryCreateAsync(first);
        await store.TryCreateAsync(second);

        await store.SetAsync(first with
        {
            Status = WorkflowOperationStatus.Failed,
            UpdatedAt = first.UpdatedAt.AddMinutes(10),
            CompletedAt = first.UpdatedAt.AddMinutes(10)
        });

        (await store.GetByMetadataPackageIdAsync($" {packageId} "))!.OperationId.Should().Be(second.OperationId);
        (await store.GetAsync(first.OperationId))!.Status.Should().Be(WorkflowOperationStatus.Failed);
        (await store.GetByMetadataPackageIdAsync("no-such-package")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryCreateAsync_MetadataRelease_UsesItsOwnRetentionUnlessTheCallerGivesOne()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var byDefault = MetadataRelease($"metadata-package-{Guid.NewGuid():N}");
        var byCaller = MetadataRelease($"metadata-package-{Guid.NewGuid():N}");
        var deploy = Deploy(NewTargetId(), DateTimeOffset.UtcNow);

        await store.TryCreateAsync(byDefault);
        await store.TryCreateAsync(byCaller, TimeSpan.FromHours(6));
        await store.TryCreateAsync(deploy);

        (await RetentionDaysAsync(harness, byDefault.OperationId)).Should().BeApproximately(30, 0.01);
        (await RetentionDaysAsync(harness, byCaller.OperationId)).Should().BeApproximately(0.25, 0.01);
        (await RetentionDaysAsync(harness, deploy.OperationId)).Should().BeApproximately(7, 0.01);
    }

    [IntegrationTest]
    public async Task QueryAsync_FiltersByKindAndStatusAndPagesNewestCreatedFirst()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var targetId = NewTargetId();
        var now = DateTimeOffset.UtcNow;
        var oldest = Deploy(targetId, now.AddMinutes(-3));
        var middle = Deploy(targetId, now.AddMinutes(-2));
        var newest = Deploy(targetId, now.AddMinutes(-1));
        foreach (var operation in new[] { middle, newest, oldest, MetadataRelease("other-kind") })
        {
            await store.TryCreateAsync(operation);
        }

        await store.SetAsync(Succeeded(oldest, now));

        var firstPage = await store.QueryAsync(new WorkflowOperationQuery { Kind = WorkflowOperationKind.Deploy, Page = 1, PageSize = 2 });
        var secondPage = await store.QueryAsync(new WorkflowOperationQuery { Kind = WorkflowOperationKind.Deploy, Page = 2, PageSize = 2 });
        var succeeded = await store.QueryAsync(new WorkflowOperationQuery { Status = WorkflowOperationStatus.Succeeded });

        firstPage.Items.Select(item => item.OperationId).Should().Equal(newest.OperationId, middle.OperationId);
        firstPage.TotalCount.Should().Be(3);
        firstPage.HasMore.Should().BeTrue();
        firstPage.IsTruncated.Should().BeFalse();
        secondPage.Items.Select(item => item.OperationId).Should().Equal(oldest.OperationId);
        secondPage.HasMore.Should().BeFalse();
        succeeded.Items.Select(item => item.OperationId).Should().Equal(oldest.OperationId);
    }

    [IntegrationTest]
    public async Task GetMostRecentSucceededDeployByTargetAsync_AfterTheNewestIsRolledBack_ReturnsThePreviousOne()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var targetId = NewTargetId();
        var now = DateTimeOffset.UtcNow;
        var older = Deploy(targetId, now.AddMinutes(-30));
        var newer = Deploy(targetId, now.AddMinutes(-10));
        (await store.GetMostRecentSucceededDeployByTargetAsync(targetId)).Should().BeNull();
        await store.TryCreateAsync(older);
        await store.TryCreateAsync(newer);
        await store.SetAsync(Succeeded(older, now.AddMinutes(-25)));
        await store.SetAsync(Succeeded(newer, now.AddMinutes(-5)));

        (await store.GetMostRecentSucceededDeployByTargetAsync(targetId))!.OperationId.Should().Be(newer.OperationId);

        await store.SetAsync(newer with { Status = WorkflowOperationStatus.RolledBack, UpdatedAt = now, CompletedAt = now });

        (await store.GetMostRecentSucceededDeployByTargetAsync(targetId))!.OperationId.Should().Be(older.OperationId);
    }

    [IntegrationTheory]
    [InlineData(WorkflowOperationStatus.Failed)]
    [InlineData(WorkflowOperationStatus.RolledBack)]
    public async Task HasLaterDeployOfTargetAsync_LaterTerminalDeploy_SupersedesDespiteAnUpdateOfTheOlderRecord(
        WorkflowOperationStatus laterStatus)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var targetId = NewTargetId();
        var now = DateTimeOffset.UtcNow;
        var stuck = Deploy(targetId, now.AddMinutes(-10)) with { Status = WorkflowOperationStatus.ManualInterventionRequired };
        var later = Deploy(targetId, now.AddMinutes(-5)) with { Status = laterStatus };
        var otherTarget = Deploy(NewTargetId(), now);

        await store.TryCreateAsync(stuck);
        await store.TryCreateAsync(otherTarget);
        (await store.HasLaterDeployOfTargetAsync(stuck)).Should().BeFalse();
        await store.TryCreateAsync(later);
        await store.SetAsync(stuck with { UpdatedAt = now.AddMinutes(1) });

        (await store.HasLaterDeployOfTargetAsync(stuck)).Should().BeTrue();
        (await store.HasLaterDeployOfTargetAsync(later)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task HasLaterDeployOfTargetAsync_OperationTheStoreDoesNotHold_IsUnknown()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();

        (await store.HasLaterDeployOfTargetAsync(Deploy(NewTargetId(), DateTimeOffset.UtcNow))).Should().BeNull();
        (await store.HasLaterDeployOfTargetAsync(MetadataRelease("not-a-deploy"))).Should().BeNull();
    }

    [IntegrationTest]
    public async Task HasLaterDeployOfTargetAsync_AfterRetentionSweep_StillSeesTheLaterDeploy()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWorkflowOperationStore();
        var targetId = NewTargetId();
        var now = DateTimeOffset.UtcNow;
        var baseline = Deploy(targetId, now.AddDays(-20));
        var later = Deploy(targetId, now.AddDays(-10));
        var unrelated = Deploy(NewTargetId(), now.AddDays(-10));
        var stillRunning = Deploy(NewTargetId(), now.AddDays(-10));
        await store.TryCreateAsync(Succeeded(baseline, now.AddDays(-20)), TimeSpan.FromDays(365));
        await store.TryCreateAsync(Succeeded(later, now.AddDays(-10)));
        await store.TryCreateAsync(Succeeded(unrelated, now.AddDays(-10)));
        await store.TryCreateAsync(stillRunning);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("workflow_operations")} SET expires_at = now() - interval '1 second' WHERE operation_id = ANY (@ids)",
            new NpgsqlParameter("ids", new[] { later.OperationId, unrelated.OperationId, stillRunning.OperationId }));

        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);

        (await store.GetAsync(unrelated.OperationId)).Should().BeNull();
        (await store.GetAsync(stillRunning.OperationId)).Should().NotBeNull("an active operation never expires");
        (await store.HasLaterDeployOfTargetAsync(baseline)).Should().BeTrue("the older deploy of the target is still retained");
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresWorkflowOperationStoreTests));

    private static async Task<double> RetentionDaysAsync(ControlPlaneStateHarness harness, string operationId)
        => await harness.ScalarAsync<double>(
            $"SELECT extract(epoch FROM expires_at - now())::float8 / 86400 FROM {harness.Table("workflow_operations")} WHERE operation_id = @id",
            new NpgsqlParameter("id", operationId));

    private static string NewTargetId() => $"target-{Guid.NewGuid():N}";

    private static WorkflowOperationRecord Succeeded(WorkflowOperationRecord operation, DateTimeOffset completedAt)
        => operation with { Status = WorkflowOperationStatus.Succeeded, UpdatedAt = completedAt, CompletedAt = completedAt };

    private static WorkflowOperationRecord Deploy(string targetId, DateTimeOffset createdAt)
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        return new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
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
    }

    private static WorkflowOperationRecord MetadataRelease(string packageId)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"metadata-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            MetadataRelease = new MetadataReleaseContext
            {
                PackageId = packageId,
                DesiredRevision = "refs/tags/metadata-release-v1",
                TargetEnvironment = "staging",
                CurrentStage = MetadataReleaseStage.Preflight
            }
        };
    }
}
