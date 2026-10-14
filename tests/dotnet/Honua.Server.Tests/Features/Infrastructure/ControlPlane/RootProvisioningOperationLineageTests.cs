// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class RootProvisioningOperationLineageTests
{
    private const string RootA = "urn:honua:provisioning:0123456789abcdef0123456789abcdef";
    private const string RootB = "urn:honua:provisioning:ffffffffffffffffffffffffffffffff";

    [Fact]
    public async Task CreateAsync_StampsTheWorkflowRecordAndTheReleaseContext()
    {
        var store = new InMemoryWorkflowOperationStore();
        var service = new MetadataReleaseControlService([store]);

        var created = await service.CreateAsync(
            Plan(),
            requestedBy: "tester",
            reason: "federated handoff",
            idempotencyKey: "lineage-release",
            correlationId: "corr-lineage",
            rootProvisioningOperationId: RootA);

        Assert.Equal(RootA, created.RootProvisioningOperationId);
        Assert.Equal(RootA, created.MetadataRelease!.RootProvisioningOperationId);
        var stored = await store.GetAsync(created.OperationId);
        Assert.Equal(RootA, stored!.RootProvisioningOperationId);
        Assert.Equal(RootA, stored.MetadataRelease!.RootProvisioningOperationId);

        var response = DeployControlEndpoints.MapOperationResponse(created);
        Assert.Equal(RootA, response.RootProvisioningOperationId);
        Assert.Equal(RootA, response.MetadataRelease!.RootProvisioningOperationId);
        var json = JsonSerializer.Serialize(response, DeployControlJsonContext.Default.DeployOperationResponse);
        Assert.Equal(2, Count(json, $"\"rootProvisioningOperationId\":\"{RootA}\""));
    }

    [Fact]
    public async Task CreateAsync_AbsentRoot_IsOmittedFromTheWireResponse()
    {
        var service = new MetadataReleaseControlService([new InMemoryWorkflowOperationStore()]);
        var created = await service.CreateAsync(Plan(), "tester", null, "lineage-plain", null);

        Assert.Null(created.RootProvisioningOperationId);
        Assert.Null(created.MetadataRelease!.RootProvisioningOperationId);
        var json = JsonSerializer.Serialize(
            DeployControlEndpoints.MapOperationResponse(created),
            DeployControlJsonContext.Default.DeployOperationResponse);
        Assert.DoesNotContain("rootProvisioningOperationId", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ReplayWithADifferentRoot_ConflictsAndKeepsTheOriginal()
    {
        var store = new InMemoryWorkflowOperationStore();
        var service = new MetadataReleaseControlService([store]);
        var created = await service.CreateAsync(Plan(), "tester", null, "lineage-release", null, rootProvisioningOperationId: RootA);

        var act = async () => await service.CreateAsync(
            Plan(), "tester", null, "lineage-release", null, rootProvisioningOperationId: RootB);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Contains("provisioning root", exception.Message, StringComparison.Ordinal);
        var stored = await store.GetAsync(created.OperationId);
        Assert.Equal(RootA, stored!.RootProvisioningOperationId);
        Assert.Equal(RootA, stored.MetadataRelease!.RootProvisioningOperationId);
    }

    [Fact]
    public async Task CreateAsync_MalformedRoot_IsRejectedBeforeTheRecordIsWritten()
    {
        var store = new InMemoryWorkflowOperationStore();
        var service = new MetadataReleaseControlService([store]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            Plan(), "tester", null, "lineage-bad", null, rootProvisioningOperationId: "not-a-root"));

        Assert.Empty(await store.ListActiveAsync());
    }

    [Fact]
    public async Task Executor_StampsTheRootFromTheVerifiedGatewayHandoff()
    {
        var store = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([store]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            RequestedBy = "agent:tester",
            RootProvisioningOperationId = RootA,
            ExecutionPayload = Payload(root: null),
        };

        var operationId = await executor.ExecuteAsync(request, request.ExecutionPayload);

        var stored = await store.GetAsync(operationId!);
        Assert.Equal(RootA, stored!.RootProvisioningOperationId);
        Assert.Equal(RootA, stored.MetadataRelease!.RootProvisioningOperationId);
    }

    [Fact]
    public async Task Executor_PayloadRoot_IsTheFallbackWhenTheCanonicalFieldIsAbsent()
    {
        var store = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([store]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            ExecutionPayload = Payload(RootA),
        };

        var operationId = await executor.ExecuteAsync(request, request.ExecutionPayload);

        Assert.Equal(RootA, (await store.GetAsync(operationId!))!.RootProvisioningOperationId);
    }

    [Fact]
    public async Task Executor_MismatchedRoots_AreRejected()
    {
        var store = new InMemoryWorkflowOperationStore();
        var executor = new MetadataReleaseOperationExecutor(new MetadataReleaseControlService([store]));
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.MetadataRelease,
            RootProvisioningOperationId = RootA,
            ExecutionPayload = Payload(RootB),
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(request, request.ExecutionPayload));
        Assert.Contains("provisioning root", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListActiveAsync());
    }

    [Fact]
    public void OperationHandleAndStatus_SerializeTheCanonicalFieldName()
    {
        var now = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var handle = new OperationHandle
        {
            OperationInstanceId = "opinst-lineage",
            OperationId = "lineage.echo",
            CorrelationId = "corr-lineage",
            Status = OperationHandleStatus.Accepted,
            CreatedAt = now,
            UpdatedAt = now,
            RootProvisioningOperationId = RootA,
        };
        var status = new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            Status = handle.Status,
            CreatedAt = now,
            UpdatedAt = now,
            RootProvisioningOperationId = handle.RootProvisioningOperationId,
        };

        var handleJson = JsonSerializer.Serialize(handle, OperationsJsonContext.Default.OperationHandle);
        var statusJson = JsonSerializer.Serialize(status, OperationsJsonContext.Default.OperationStatus);
        Assert.Contains($"\"rootProvisioningOperationId\":\"{RootA}\"", handleJson, StringComparison.Ordinal);
        Assert.Contains($"\"rootProvisioningOperationId\":\"{RootA}\"", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rootProvisioningOperationId",
            JsonSerializer.Serialize(handle with { RootProvisioningOperationId = null }, OperationsJsonContext.Default.OperationHandle),
            StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static MetadataReleaseExecutionPlan Plan() => new()
    {
        PackageId = "pkg-lineage",
        TargetEnvironment = "production",
        ResourceSemanticId = "parcels",
        NewFieldName = "owner_email",
        Script = new MetadataReleaseScript
        {
            ScriptId = "add-owner_email",
            Reversible = true,
            ForwardOperations =
            [
                new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.AddColumn,
                    ResourceSemanticId = "parcels",
                    FieldName = "owner_email",
                    FieldType = "String",
                    Nullable = true,
                },
            ],
        },
    };

    private static string Payload(string? root)
    {
        var rootField = root is null ? string.Empty : $",\"rootProvisioningOperationId\":\"{root}\"";
        return "{\"action\":\"create\",\"packageId\":\"pkg-lineage\",\"targetEnvironment\":\"production\","
            + "\"resourceSemanticId\":\"parcels\",\"newFieldName\":\"owner_email\""
            + rootField
            + "}";
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.Values.FirstOrDefault(operation => operation.MetadataRelease?.PackageId == packageId));

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(_operations.Values.ToArray());
    }
}
