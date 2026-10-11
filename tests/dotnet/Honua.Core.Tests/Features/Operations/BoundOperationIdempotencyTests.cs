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
        Assert.DoesNotContain(first.EvidenceRefs, reference => reference.StartsWith("acceptance-attempt:", StringComparison.Ordinal));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_CreatePersistsThenThrows_FailsOnlyItsUnauditedEnvelope(bool cancel)
    {
        var store = new HookInstanceStore
        {
            AfterCreate = () => cancel
                ? Task.FromException(new OperationCanceledException())
                : Task.FromException(new InvalidOperationException("Create reply lost")),
        };
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var context = Context() with { OperationInstanceId = "uncertain-create" };
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateAcceptedAsync("admin.import.upload-url", context));
        }
        else
        {
            Assert.Equal(OperationHandleStatus.Failed, (await factory.CreateAcceptedAsync("admin.import.upload-url", context)).Status);
        }

        var persisted = await store.GetAsync(context.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Failed, persisted!.Status);
        Assert.Equal(1, persisted.Version);
        Assert.Null(persisted.AuditId);
    }

    [Fact]
    public async Task Acceptance_CreateReplyLostOnCompetingAttempt_DoesNotFailTheWinner()
    {
        var store = new HookInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var context = Context() with { OperationInstanceId = "other-attempt", CorrelationId = "same-correlation" };
        store.AfterCreate = async () =>
        {
            // Simulate a rival winning NX before this attempt receives a failure.
            var persisted = await store.GetAsync(context.OperationInstanceId);
            await store.ReplaceInitialAsync(persisted! with { EvidenceRefs = ["acceptance-attempt:other-owner"] });
            throw new InvalidOperationException("Create reply lost");
        };
        Assert.Equal(OperationHandleStatus.Failed, (await factory.CreateAcceptedAsync("admin.import.upload-url", context)).Status);
        var winner = await store.GetAsync(context.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Accepted, winner!.Status);
        Assert.Equal(0, winner.Version);
    }

    [Fact]
    public async Task Acceptance_CreateReplyLost_ConcurrentProgressWinsRecoveryCompareAndSet()
    {
        var store = new HookInstanceStore
        {
            AfterCreate = () => Task.FromException(new InvalidOperationException("Create reply lost")),
        };
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var context = Context() with { OperationInstanceId = "progressed-create" };
        store.BeforeCompareAndSet = async () =>
        {
            var persisted = await store.GetAsync(context.OperationInstanceId);
            await store.SetAsync(persisted! with { Status = OperationHandleStatus.Completed, JobId = "winner-job" });
        };
        Assert.Equal(OperationHandleStatus.Failed, (await factory.CreateAcceptedAsync("admin.import.upload-url", context)).Status);
        var winner = await store.GetAsync(context.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Completed, winner!.Status);
        Assert.Equal("winner-job", winner.JobId);
    }

    private sealed class HookInstanceStore : IOperationInstanceStore
    {
        private VolatileOperationInstanceStore _inner = new();
        public Func<Task>? BeforeGet { get; set; }
        public Func<Task>? AfterCreate { get; set; }
        public Func<Task>? BeforeCompareAndSet { get; set; }
        public async Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        {
            var created = await _inner.TryCreateAsync(envelope, cancellationToken);
            if (AfterCreate is not null) await AfterCreate();
            return created;
        }
        public async Task ReplaceInitialAsync(OperationHandle envelope)
        {
            _inner = new VolatileOperationInstanceStore();
            await _inner.TryCreateAsync(envelope);
        }
        public async Task<bool> TrySetAsync(OperationHandle envelope, long expectedVersion, CancellationToken cancellationToken = default)
        {
            if (BeforeCompareAndSet is not null) await BeforeCompareAndSet();
            return await _inner.TrySetAsync(envelope, expectedVersion, cancellationToken);
        }
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
