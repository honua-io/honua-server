// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseOperationEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryWorkflowOperationStore _workflowStore = new();
    private readonly RecordingDestructiveApprovalEvaluator _approvalEvaluator = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MetadataReleaseOperationEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowOperationStore>();
                services.RemoveAll<IDeployTargetRegistry>();
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IWorkflowOperationStore>(_workflowStore);
                services.AddSingleton<IDeployTargetRegistry>(new EmptyDeployTargetRegistry());
                services.AddSingleton<IOperatorApprovalEvaluator>(_approvalEvaluator);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/releases/{packageId}/operation")]
    public async Task GetMetadataReleaseOperation_ByPackageId_ReturnsTimelineStateAndRollbackPlan()
    {
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var operation = CreateMetadataReleaseOperation(packageId, MetadataRollbackClass.MetadataOnly);
        (await _workflowStore.TryCreateAsync(operation)).Should().BeTrue();

        var response = await _client.GetAsync($"/api/v1/admin/metadata/releases/{packageId}/operation");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("operationId").GetString().Should().Be(operation.OperationId);
        root.GetProperty("kind").GetString().Should().Be("MetadataRelease");
        root.GetProperty("status").GetString().Should().Be("Reconciling");
        root.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).Should().Contain("operation-warning");
        root.GetProperty("blockingReasons").EnumerateArray().Select(item => item.GetString()).Should().Contain("operation-blocker");

        var metadataRelease = root.GetProperty("metadataRelease");
        metadataRelease.GetProperty("packageId").GetString().Should().Be(packageId);
        metadataRelease.GetProperty("gitOperationId").GetString().Should().Be("git-write-42");
        metadataRelease.GetProperty("deployOperationId").GetString().Should().Be("deploy-linked-1");
        metadataRelease.GetProperty("jobIds").EnumerateArray().Select(item => item.GetString()).Should().Contain(["job-backup-1", "job-smoke-1"]);
        metadataRelease.GetProperty("evidenceRefs")[0].GetProperty("kind").GetString().Should().Be("compatibility-prevalidation");
        metadataRelease.GetProperty("currentStage").GetString().Should().Be("Preflight");
        metadataRelease.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).Should().Contain("metadata-warning");
        metadataRelease.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()).Should().Contain("metadata-blocker");

        var rollbackPlan = metadataRelease.GetProperty("rollbackPlan");
        rollbackPlan.GetProperty("class").GetString().Should().Be("MetadataOnly");
        rollbackPlan.GetProperty("isDataAffecting").GetBoolean().Should().BeFalse();
        rollbackPlan.GetProperty("requiresExplicitApproval").GetBoolean().Should().BeFalse();
        rollbackPlan.GetProperty("steps")[0].GetString().Should().Be("Revert metadata package commit.");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/operations/{operationId}")]
    public async Task GetDeployOperation_ByOperationId_ReturnsMetadataReleaseFailureAndRollbackPlan()
    {
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var operation = CreateMetadataReleaseOperation(
            packageId,
            MetadataRollbackClass.SnapshotRestore,
            status: WorkflowOperationStatus.Failed,
            stage: MetadataReleaseStage.Failed);
        (await _workflowStore.TryCreateAsync(operation)).Should().BeTrue();

        var response = await _client.GetAsync($"/api/v1/admin/deploy/operations/{operation.OperationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("Failed");
        var metadataRelease = root.GetProperty("metadataRelease");
        metadataRelease.GetProperty("currentStage").GetString().Should().Be("Failed");
        var rollbackPlan = metadataRelease.GetProperty("rollbackPlan");
        rollbackPlan.GetProperty("class").GetString().Should().Be("SnapshotRestore");
        rollbackPlan.GetProperty("isDataAffecting").GetBoolean().Should().BeTrue();
        rollbackPlan.GetProperty("requiresExplicitApproval").GetBoolean().Should().BeTrue();
        rollbackPlan.GetProperty("evidenceRequired").EnumerateArray().Select(item => item.GetString()).Should().Contain("snapshot-id");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task RollbackDeployOperation_MetadataOnly_UsesNonDestructiveApprovalPath()
    {
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var operation = CreateMetadataReleaseOperation(
            packageId,
            MetadataRollbackClass.MetadataOnly,
            status: WorkflowOperationStatus.Failed,
            stage: MetadataReleaseStage.Failed);
        (await _workflowStore.TryCreateAsync(operation)).Should().BeTrue();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operation.OperationId}/rollback",
            new { reason = "metadata-only rollback" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _approvalEvaluator.DestructiveFlags.Should().ContainSingle().Which.Should().BeFalse();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("RollbackRequested");
        root.GetProperty("metadataRelease").GetProperty("currentStage").GetString().Should().Be("RollbackRequested");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    public async Task RollbackDeployOperation_DataAffecting_RequiresApproval()
    {
        var packageId = $"metadata-package-{Guid.NewGuid():N}";
        var operation = CreateMetadataReleaseOperation(
            packageId,
            MetadataRollbackClass.SnapshotRestore,
            status: WorkflowOperationStatus.Failed,
            stage: MetadataReleaseStage.Failed);
        (await _workflowStore.TryCreateAsync(operation)).Should().BeTrue();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/deploy/operations/{operation.OperationId}/rollback",
            new { reason = "restore snapshot" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _approvalEvaluator.DestructiveFlags.Should().ContainSingle().Which.Should().BeTrue();
        var stored = await _workflowStore.GetAsync(operation.OperationId);
        stored!.Status.Should().Be(WorkflowOperationStatus.Failed);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/releases/{packageId}/operation")]
    public async Task GetMetadataReleaseOperation_UnknownPackageId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/metadata/releases/metadata-package-{Guid.NewGuid():N}/operation");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static WorkflowOperationRecord CreateMetadataReleaseOperation(
        string packageId,
        MetadataRollbackClass rollbackClass,
        WorkflowOperationStatus status = WorkflowOperationStatus.Reconciling,
        MetadataReleaseStage stage = MetadataReleaseStage.Preflight)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowOperationRecord
        {
            OperationId = $"metadata-release-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = status == WorkflowOperationStatus.Failed ? now : null,
            CurrentPhase = "Metadata release lifecycle running.",
            Warnings = ["operation-warning"],
            BlockingReasons = ["operation-blocker"],
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
                CurrentStage = stage,
                RollbackPlan = new MetadataRollbackPlan
                {
                    Class = rollbackClass,
                    RequiresExplicitApproval = rollbackClass is not MetadataRollbackClass.MetadataOnly,
                    Steps = rollbackClass == MetadataRollbackClass.MetadataOnly
                        ? ["Revert metadata package commit."]
                        : ["Verify snapshot evidence.", "Restore snapshot.", "Re-run smoke tests."],
                    EvidenceRequired = rollbackClass == MetadataRollbackClass.MetadataOnly
                        ? []
                        : ["snapshot-id", "operator-approval"],
                    ApprovalPolicyRef = rollbackClass == MetadataRollbackClass.MetadataOnly
                        ? null
                        : "operator.destructive.deployment"
                },
                Warnings = ["metadata-warning"],
                Blockers = ["metadata-blocker"]
            }
        };
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                IndexMetadataReleaseOperation(operation);
            }

            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            IndexMetadataReleaseOperation(operation);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void IndexMetadataReleaseOperation(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }

    private sealed class EmptyDeployTargetRegistry : IDeployTargetRegistry
    {
        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(null);
    }

    private sealed class RecordingDestructiveApprovalEvaluator : IOperatorApprovalEvaluator
    {
        private readonly ConcurrentQueue<bool> _destructiveFlags = new();

        public IReadOnlyList<bool> DestructiveFlags => _destructiveFlags.ToArray();

        public ApprovalRequirement Evaluate(
            System.Security.Claims.ClaimsPrincipal principal,
            OperatorAuthorizationRequest request)
        {
            _destructiveFlags.Enqueue(request.IsDestructive);
            return request.IsDestructive
                ? ApprovalRequirement.Required("operator.destructive.deployment", "destructive-action-requires-approval")
                : ApprovalRequirement.NotRequired();
        }
    }
}
