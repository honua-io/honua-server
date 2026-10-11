// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Tests.OperationScopes;

public sealed class BoundOperationIdempotencyTests
{
    [Fact]
    public async Task Acceptance_ConcurrentRetry_DoesNotGrantASecondActuation()
    {
        var store = new VolatileOperationInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var context = Context();
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", context);
        var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", context);

        Assert.Equal(OperationHandleStatus.Accepted, first.Status);
        Assert.Equal(OperationHandleStatus.Failed, retry.Status);
        Assert.Equal("409", retry.Result!.Details["statusCode"]);
        Assert.Equal(OperationHandleStatus.Accepted, (await store.GetAsync(first.OperationInstanceId))!.Status);
    }

    [Fact]
    public async Task Acceptance_CompletedRetry_ReplaysThePersistedJobAcrossFactoryInstances()
    {
        var store = new VolatileOperationInstanceStore();
        var audit = new VolatileOperationAuditLog();
        var factory = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.Queued, JobId = "original-job" });

        var restarted = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var retry = await restarted.CreateAcceptedAsync("admin.import.upload-url", Context());

        Assert.Equal(first.OperationInstanceId, retry.OperationInstanceId);
        Assert.Equal(first.AuditId, retry.AuditId);
        Assert.Equal("original-job", retry.JobId);
        Assert.Equal(OperationHandleStatus.Queued, retry.Status);
    }

    [Fact]
    public async Task Acceptance_ChangedPayload_ConflictsWithoutOverwritingTheOriginalJob()
    {
        var store = new VolatileOperationInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.Queued, JobId = "original-job" });

        var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", Context() with
        {
            IdempotencyRequestHash = new string('b', 64),
        });

        Assert.Equal(OperationHandleStatus.Failed, retry.Status);
        Assert.Equal("409", retry.Result!.Details["statusCode"]);
        Assert.Equal("original-job", (await store.GetAsync(first.OperationInstanceId))!.JobId);
    }

    private static OperationPolicyContext Context() => new()
    {
        PrincipalId = "operator-a",
        TenantId = "tenant-a",
        IdempotencyKey = "principal-scoped-key",
        IdempotencyRequestHash = new string('a', 64),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_RetryAuditThrowsOrCancels_NeverOverwritesOwnersCompletedJob(bool cancel)
    {
        var store = new VolatileOperationInstanceStore();
        var audit = new HookAuditLog();
        var factory = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.Queued, JobId = "original-job" });
        audit.OnRecord = async () =>
        {
            await store.SetAsync(first with { Status = OperationHandleStatus.Completed, JobId = "original-job" });
            if (cancel) throw new OperationCanceledException();
            throw new InvalidOperationException("Audit unavailable");
        };

        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateAcceptedAsync("admin.import.upload-url", Context()));
        }
        else
        {
            var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
            Assert.Equal(OperationHandleStatus.Failed, retry.Status);
        }
        var persisted = await store.GetAsync(first.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Completed, persisted!.Status);
        Assert.Equal("original-job", persisted.JobId);
    }

    [Fact]
    public async Task Acceptance_OwnerReclaimsDuringRetryAudit_NeverGrantsRetryActuation()
    {
        var store = new VolatileOperationInstanceStore();
        var audit = new HookAuditLog();
        var factory = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.RequiresApproval });
        audit.OnRecord = () => store.SetAsync(first with { Status = OperationHandleStatus.Accepted });
        var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        Assert.Equal(OperationHandleStatus.Failed, retry.Status);
        Assert.Equal("409", retry.Result!.Details["statusCode"]);
        Assert.Equal(OperationHandleStatus.Accepted, (await store.GetAsync(first.OperationInstanceId))!.Status);
    }

    [Fact]
    public async Task Acceptance_WorkerCompletesDuringRetryAudit_ReturnsLatestWithoutLostUpdate()
    {
        var store = new VolatileOperationInstanceStore();
        var audit = new HookAuditLog();
        var factory = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.Queued, JobId = "original-job" });
        audit.OnRecord = () => store.SetAsync(first with { Status = OperationHandleStatus.Completed, JobId = "original-job" });

        var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        Assert.Equal(OperationHandleStatus.Completed, retry.Status);
        Assert.Equal(OperationHandleStatus.Completed, (await store.GetAsync(first.OperationInstanceId))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_RetryReadThrowsOrCancels_NeverOverwritesTheOwner(bool cancel)
    {
        var store = new HookInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("admin.import.upload-url", Context());
        await store.SetAsync(first with { Status = OperationHandleStatus.Completed, JobId = "original-job" });
        store.BeforeGet = () => cancel
            ? Task.FromException(new OperationCanceledException())
            : Task.FromException(new InvalidOperationException("Read unavailable"));
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateAcceptedAsync("admin.import.upload-url", Context()));
        }
        else
        {
            Assert.Equal(OperationHandleStatus.Failed, (await factory.CreateAcceptedAsync("admin.import.upload-url", Context())).Status);
        }
        store.BeforeGet = null;
        var persisted = await store.GetAsync(first.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Completed, persisted!.Status);
        Assert.Equal("original-job", persisted.JobId);
    }

    private sealed class HookInstanceStore : IOperationInstanceStore
    {
        private readonly VolatileOperationInstanceStore _inner = new();
        public Func<Task>? BeforeGet { get; set; }
        public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default) => _inner.TryCreateAsync(envelope, cancellationToken);
        public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default) => _inner.SetAsync(envelope, cancellationToken);
        public async Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
        {
            if (BeforeGet is not null) await BeforeGet();
            return await _inner.GetAsync(operationInstanceId, cancellationToken);
        }
    }

    private sealed class HookAuditLog : IAuditLog
    {
        public Func<Task>? OnRecord { get; set; }
        public async Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (OnRecord is not null) await OnRecord();
            return "audit-identity";
        }
    }
}
