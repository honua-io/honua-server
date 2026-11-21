// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class AuditingFeatureWriterTests
{
    private readonly CapturingAuditLog _auditLog = new();
    private readonly StubFeatureWriter _inner = new();
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuditPolicyOptions _policy = new();
    private readonly AuditingFeatureWriter _writer;

    public AuditingFeatureWriterTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "operator@example.com") },
            authenticationType: "Test"));
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        _httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        _writer = new AuditingFeatureWriter(_inner, _auditLog, new FeatureWriteAuditScope(_httpContextAccessor, () => _policy));
    }

    [Fact]
    public async Task DeleteAsync_WhenDeleted_EmitsFeatureDeleteSuccess()
    {
        _inner.DeleteResult = true;

        var result = await _writer.DeleteAsync(layerId: 7, featureId: 42);

        result.Should().BeTrue();
        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.DataDelete);
        evt.Action.Should().Be("feature.delete");
        evt.Outcome.Should().Be(AuditOutcome.Success);
        evt.ResourceType.Should().Be("feature");
        evt.ResourceId.Should().Be("7");
        evt.Actor.Should().Be("operator@example.com");
        evt.ActorType.Should().Be(AuditActorType.UserId);
        evt.Details.Should().Contain("\"objectId\":42");
        evt.Details.Should().NotContain("connection");
    }

    [Fact]
    public async Task DeleteAsync_WhenNotFound_EmitsFailureOutcome()
    {
        _inner.DeleteResult = false;

        await _writer.DeleteAsync(layerId: 3, featureId: 99);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.delete");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task DeleteAsync_WhenInnerThrows_EmitsFailureAndRethrows()
    {
        _inner.ThrowOnDelete = true;

        var act = async () => await _writer.DeleteAsync(layerId: 1, featureId: 5);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task ApplyEditsAsync_WithDeletes_EmitsBulkDeleteEvent()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 0, updatedCount: 0, deletedCount: 2);
        var batch = FeatureEditBatch.Create(deletes: ImmutableArray.Create(1L, 2L));

        await _writer.ApplyEditsAsync(layerId: 11, batch);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.DataDelete);
        evt.Action.Should().Be("feature.bulk-edit.delete");
        evt.Outcome.Should().Be(AuditOutcome.Success);
        evt.Details.Should().Contain("\"deleted\":2");
    }

    [Fact]
    public async Task ApplyEditsAsync_WithMultipleCreatesNoDelete_EmitsBulkEdit()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 3, updatedCount: 0, deletedCount: 0);
        var creates = ImmutableArray.Create(NewFeature(), NewFeature(), NewFeature());
        var batch = FeatureEditBatch.Create(creates: creates);

        await _writer.ApplyEditsAsync(layerId: 4, batch);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.bulk-edit");
        evt.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task ApplyEditsAsync_SingleCreate_DoesNotEmit()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 1, updatedCount: 0, deletedCount: 0);
        var batch = FeatureEditBatch.Create(creates: ImmutableArray.Create(NewFeature()));

        await _writer.ApplyEditsAsync(layerId: 4, batch);

        _auditLog.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_And_UpdateAsync_AreNotAudited()
    {
        await _writer.CreateAsync(2, NewFeature());
        await _writer.UpdateAsync(2, NewFeature());

        _auditLog.Events.Should().BeEmpty();
    }

    [UnitTest]
    public async Task CreateAndUpdate_WithSingleWriteAuditOn_RecordEachWrite()
    {
        _policy.RecordSingleFeatureWrites = true;

        await _writer.CreateAsync(2, NewFeature(id: 17));
        await _writer.UpdateAsync(2, NewFeature(id: 18));

        _auditLog.Events.Select(static e => e.Action).Should().Equal("feature.create", "feature.update");
        _auditLog.Events.Should().OnlyContain(static e => e.Outcome == AuditOutcome.Success
            && e.ResourceId == "2"
            && e.Actor == "operator@example.com");
        _auditLog.Events[0].Details.Should().Be("{\"objectId\":17}");
    }

    [UnitTest]
    public async Task SingleWrites_WithSingleWriteAuditOn_WhenInnerThrows_RecordFailureAndRethrow()
    {
        _policy.RecordSingleFeatureWrites = true;
        _inner.ThrowOnWrite = true;

        var update = async () => await _writer.UpdateAsync(5, NewFeature(id: 9));
        var create = async () => await _writer.CreateAsync(5, NewFeature());

        await update.Should().ThrowAsync<InvalidOperationException>();
        await create.Should().ThrowAsync<InvalidOperationException>();
        _auditLog.Events.Select(static e => e.Action).Should().Equal("feature.update", "feature.create");
        _auditLog.Events.Should().OnlyContain(static e => e.Outcome == AuditOutcome.Failure);
    }

    [UnitTest]
    public async Task Create_WithSingleWriteAuditOn_RecordsEvenWhenTheCallerCancelsAfterTheWrite()
    {
        _policy.RecordSingleFeatureWrites = true;
        using var cancellation = new CancellationTokenSource();
        _inner.AfterWrite = cancellation.Cancel;

        await _writer.CreateAsync(2, NewFeature(id: 4), cancellation.Token);

        _auditLog.Events.Should().ContainSingle().Which.Action.Should().Be("feature.create");
        _auditLog.Tokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeFalse();
    }

    [UnitTest]
    public async Task SingleCreateBatch_WithSingleWriteAuditOn_RecordsEdit()
    {
        _policy.RecordSingleFeatureWrites = true;
        _inner.EditResult = FeatureEditResult.Success(createdCount: 1, updatedCount: 0, deletedCount: 0);

        await _writer.ApplyEditsAsync(layerId: 4, FeatureEditBatch.Create(creates: ImmutableArray.Create(NewFeature())));
        await _writer.ApplyEditsAsync(layerId: 4, FeatureEditBatch.Create());

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.edit");
        evt.Details.Should().Contain("\"created\":1");
    }

    [UnitTest]
    public async Task TransactionalSingleUpdate_WithSingleWriteAuditOn_RecordsEditOnCommit()
    {
        _policy.RecordSingleFeatureWrites = true;
        _inner.EditResult = FeatureEditResult.Success(createdCount: 0, updatedCount: 1, deletedCount: 0);
        var batch = FeatureEditBatch.Create(updates: ImmutableArray.Create(NewFeature(id: 3)));

        await using (var transaction = await _writer.BeginTransactionAsync())
        {
            await transaction.ApplyEditsAsync(layerId: 6, batch);
            _auditLog.Events.Should().BeEmpty();
            await transaction.CommitAsync();
        }

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.edit");
        evt.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task Transaction_DisposeWithoutCompletion_EmitsBulkFailureOnce()
    {
        var batch = FeatureEditBatch.Create(deletes: ImmutableArray.Create(42L, 43L));
        await using (var transaction = await _writer.BeginTransactionAsync())
        {
            await transaction.ApplyEditsAsync(layerId: 7, batch);
        }

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.bulk-edit.delete");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
        _inner.Transaction!.RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task Transaction_ExplicitRollbackThenDispose_EmitsBulkFailureOnce()
    {
        var batch = FeatureEditBatch.Create(deletes: ImmutableArray.Create(42L, 43L));
        await using (var transaction = await _writer.BeginTransactionAsync())
        {
            await transaction.ApplyEditsAsync(layerId: 7, batch);
            await transaction.RollbackAsync();
        }

        _auditLog.Events.Should().ContainSingle();
    }

    [UnitTheory]
    [InlineData(FeatureWriterTransactionCommitOutcome.Committed, AuditOutcome.Success)]
    [InlineData(FeatureWriterTransactionCommitOutcome.Unknown, AuditOutcome.Failure)]
    [InlineData(FeatureWriterTransactionCommitOutcome.SupersededByExistingReceipt, AuditOutcome.Failure)]
    public async Task Transaction_ReceiptCommit_ForwardsReceiptAndAuditsAcknowledgedOutcome(
        FeatureWriterTransactionCommitOutcome outcome, AuditOutcome expectedAudit)
    {
        _inner.CommitOutcome = outcome;
        _inner.EditResult = FeatureEditResult.Success(0, 0, 2);
        var receipt = new FeatureWriterTransactionReceipt
        {
            Scope = "transaction-retry-scope",
            Payload = new byte[] { 1, 2 },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        using var cancellation = new CancellationTokenSource();
        await using (var transaction = await _writer.BeginTransactionAsync())
        {
            transaction.SupportsDurableReceipts.Should().BeTrue();
            await transaction.ApplyEditsAsync(7, FeatureEditBatch.Create(deletes: [42L, 43L]));
            (await transaction.CommitAsync(receipt, cancellation.Token)).Should().Be(outcome);
        }

        _inner.Transaction!.Receipt.Should().Be(receipt);
        _inner.Transaction.CommitCancellation.Should().Be(cancellation.Token);
        _auditLog.Events.Should().ContainSingle().Which.Outcome.Should().Be(expectedAudit);
    }

    private static Feature NewFeature(long id = 0) => new()
    {
        Id = id,
        Geometry = null,
        Attributes = ImmutableDictionary<string, object?>.Empty,
    };

    private sealed class StubFeatureWriter : IFeatureWriter
    {
        public StubFeatureWriterTransaction? Transaction { get; private set; }

        public FeatureWriterTransactionCommitOutcome CommitOutcome { get; set; } =
            FeatureWriterTransactionCommitOutcome.Committed;

        public bool DeleteResult { get; set; } = true;

        public bool ThrowOnDelete { get; set; }

        public bool ThrowOnWrite { get; set; }

        public FeatureEditResult EditResult { get; set; } =
            FeatureEditResult.Success(0, 0, 0);

        public Action? AfterWrite { get; set; }

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite)
            {
                return Task.FromException<Feature>(new InvalidOperationException("create failed"));
            }

            AfterWrite?.Invoke();
            return Task.FromResult(feature);
        }

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => ThrowOnWrite
                ? Task.FromException<Feature>(new InvalidOperationException("update failed"))
                : Task.FromResult(feature);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("delete failed");
            }

            return Task.FromResult(DeleteResult);
        }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EditResult);

        public Task<IFeatureWriterTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            Transaction = new StubFeatureWriterTransaction(this);
            return Task.FromResult<IFeatureWriterTransaction>(Transaction);
        }
    }

    private sealed class StubFeatureWriterTransaction(StubFeatureWriter owner) : IFeatureWriterTransaction
    {
        public bool RolledBack { get; private set; }

        public bool SupportsDurableReceipts => true;
        public FeatureWriterTransactionReceipt? Receipt { get; private set; }
        public CancellationToken CommitCancellation { get; private set; }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
            FeatureWriterTransactionReceipt receipt, CancellationToken cancellationToken = default)
        {
            Receipt = receipt;
            CommitCancellation = cancellationToken;
            return Task.FromResult(owner.CommitOutcome);
        }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => owner.ApplyEditsAsync(layerId, editBatch, cancellationToken);

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureWriterTransactionCommitOutcome.Committed);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (!RolledBack)
            {
                await RollbackAsync(CancellationToken.None);
            }
        }
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public List<CancellationToken> Tokens { get; } = new();

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            Tokens.Add(cancellationToken);
            return Task.FromResult<string?>("audit-test");
        }
    }
}
