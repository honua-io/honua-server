// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class PostgresProposalPersistenceTests
{
    private IOperationInstanceStore Instances => _fixture.GetService<IOperationInstanceStore>();

    [IntegrationTest]
    public async Task TryCreateAsync_OperationInstanceWithoutRedis_SurvivesHostRestart()
    {
        var handle = Handle();
        Assert.True(await Instances.TryCreateAsync(handle));
        await _fixture.RestartHostAsync();

        var restored = Assert.IsType<OperationHandle>(await Instances.GetAsync(handle.OperationInstanceId));
        Assert.Equal(handle.CorrelationId, restored.CorrelationId);
        Assert.Equal(handle.AuditId, restored.AuditId);
        Assert.Equal(handle.TenantId, restored.TenantId);
        Assert.Equal(handle.EvidenceRefs, restored.EvidenceRefs);
        Assert.False(await Instances.TryCreateAsync(handle));
    }

    [IntegrationTest]
    public async Task TrySetAsync_OperationInstanceConcurrentUpdates_OneWinsAndStaleCannotReopen()
    {
        var handle = Handle();
        Assert.True(await Instances.TryCreateAsync(handle));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 8).Select(async index =>
        {
            await start.Task;
            return await Instances.TrySetAsync(handle with
            {
                Status = OperationHandleStatus.Completed,
                Reason = $"writer-{index}"
            }, expectedVersion: 0);
        }).ToArray();
        start.SetResult();
        Assert.Single(await Task.WhenAll(writes), won => won);
        var restored = Assert.IsType<OperationHandle>(await Instances.GetAsync(handle.OperationInstanceId));
        Assert.Equal(1, restored.Version);
        Assert.Equal(OperationHandleStatus.Completed, restored.Status);
        Assert.False(await Instances.TrySetAsync(handle, expectedVersion: 0));
        Assert.DoesNotContain(await Instances.ListActiveAsync(), item => item.OperationInstanceId == handle.OperationInstanceId);
        Assert.False(await Instances.TrySetAsync(Handle(), expectedVersion: 0));
    }

    [IntegrationTest]
    public async Task TryAcquireLeaseAsync_OperationInstanceHostRestarts_LeaseRemainsExclusive()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.True(await Instances.TryAcquireLeaseAsync(id, "owner", TimeSpan.FromMinutes(1)));
        await _fixture.RestartHostAsync();
        Assert.False(await Instances.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        await Instances.ReleaseLeaseAsync(id, "other");
        Assert.False(await Instances.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        await Instances.ReleaseLeaseAsync(id, "owner");
        Assert.True(await Instances.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        await Instances.ReleaseLeaseAsync(id, "other");
    }

    [IntegrationTest]
    public async Task CreateAcceptedAsync_IdempotentOperationWithoutRedis_RetainsIdentityAfterRestart()
    {
        var context = new OperationPolicyContext
        {
            TenantId = "tenant-a",
            PrincipalId = "requester",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            IdempotencyRequestHash = "request-sha",
            CorrelationId = "original-correlation"
        };
        var accepted = await _fixture.GetService<IOperationEnvelopeFactory>()
            .CreateAcceptedAsync("admin.role.delete", context);
        Assert.Equal(OperationHandleStatus.Accepted, accepted.Status);
        var persisted = Assert.IsType<OperationHandle>(await Instances.GetAsync(accepted.OperationInstanceId));
        Assert.True(await Instances.TrySetAsync(persisted with { Status = OperationHandleStatus.Completed }, persisted.Version));
        var completed = Assert.IsType<OperationHandle>(await Instances.GetAsync(accepted.OperationInstanceId));
        await _fixture.RestartHostAsync();

        var retry = await _fixture.GetService<IOperationEnvelopeFactory>()
            .CreateAcceptedAsync("admin.role.delete", context with { CorrelationId = "retry-correlation" });

        Assert.Equal(accepted.OperationInstanceId, retry.OperationInstanceId);
        Assert.Equal(accepted.AuditId, retry.AuditId);
        Assert.Equal(accepted.CorrelationId, retry.CorrelationId);
        Assert.Equal(completed.Version, retry.Version);
        Assert.Equal(OperationHandleStatus.Completed, retry.Status);
        var conflict = await _fixture.GetService<IOperationEnvelopeFactory>()
            .CreateAcceptedAsync("admin.role.delete", context with { IdempotencyRequestHash = "different-request" });
        Assert.Equal(OperationHandleStatus.Failed, conflict.Status);
    }

    private static OperationHandle Handle() => new()
    {
        OperationInstanceId = Guid.NewGuid().ToString("N"),
        OperationId = "admin.role.delete",
        Status = OperationHandleStatus.Accepted,
        CorrelationId = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TenantId = "tenant-a",
        AuditId = "durable-audit",
        EvidenceRefs = ["request-evidence"]
    };
}
