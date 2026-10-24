// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class FeatureDataAccessEditReceiptTests
{
    [Fact]
    public async Task CommitStagedEditAsync_WritesTheReceiptOnTheEditTransaction_BeforeCommit()
    {
        var transaction = new RecordingTransaction();
        DbTransaction? writeTransaction = null;
        var committedDuringWrite = false;
        var result = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 0,
            createResults: ImmutableArray.Create(EditOperationResult.Success(41)));
        var receipt = new FeatureEditIdempotencyReceipt
        {
            Scope = new string('a', 64),
            ExpiresAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
            Payload = staged =>
            {
                staged.CreatedCount.Should().Be(1);
                return new byte[] { 0x01, 0x02 };
            },
        };

        var stagedCommit = await FeatureDataAccess.CommitStagedEditAsync(
            transaction,
            result,
            receipt,
            (editTransaction, scope, payload, expiresAt, _) =>
            {
                writeTransaction = editTransaction;
                committedDuringWrite = transaction.Committed;
                scope.Should().Be(receipt.Scope);
                payload.ToArray().Should().Equal(0x01, 0x02);
                expiresAt.Should().Be(receipt.ExpiresAt);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        writeTransaction.Should().BeSameAs(transaction);
        committedDuringWrite.Should().BeFalse();
        transaction.CommitCalls.Should().Be(1);
        transaction.RollbackCalls.Should().Be(0);
        stagedCommit.ReceiptDurable.Should().BeTrue();
        stagedCommit.SupersededByExistingReceipt.Should().BeFalse();
    }

    [Fact]
    public async Task CommitStagedEditAsync_ReceiptWriteFailure_DoesNotCommit()
    {
        var transaction = new RecordingTransaction();
        var receipt = Receipt(_ => new byte[] { 0x01 });

        var act = async () => await FeatureDataAccess.CommitStagedEditAsync(
            transaction,
            FeatureEditResult.Success(1, 0, 0),
            receipt,
            (_, _, _, _, _) => throw new InvalidOperationException("receipt insert failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        transaction.CommitCalls.Should().Be(0);
        transaction.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task CommitStagedEditAsync_LiveReceipt_RollsBackInsteadOfCommitting()
    {
        var transaction = new RecordingTransaction();

        var stagedCommit = await FeatureDataAccess.CommitStagedEditAsync(
            transaction,
            FeatureEditResult.Success(1, 0, 0),
            Receipt(_ => new byte[] { 0x01 }),
            (_, _, _, _, _) => Task.FromResult(false),
            CancellationToken.None);

        transaction.CommitCalls.Should().Be(0);
        transaction.RollbackCalls.Should().Be(1);
        stagedCommit.ReceiptDurable.Should().BeFalse();
        stagedCommit.SupersededByExistingReceipt.Should().BeTrue();
    }

    [Fact]
    public void ReceiptSql_MatchesTheMigrationTableAndPayloadBound()
    {
        // Segments are relative literals. RepositoryPaths joins them onto the
        // repository root and does not drop earlier segments when a later one is rooted.
        var migration = File.ReadAllText(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "130_CreateGrpcApplyEditsReceipt.sql"));
        var insert = PostgresGrpcApplyEditsReceipt.InsertSql("honua.grpc_apply_edits_receipt");
        var select = PostgresGrpcApplyEditsReceipt.SelectSql("honua.grpc_apply_edits_receipt");
        var deleteExpired = PostgresGrpcApplyEditsReceipt.DeleteExpiredSql("honua.grpc_apply_edits_receipt");

        migration.Should().Contain("grpc_apply_edits_receipt");
        migration.Should().Contain("scope_key");
        migration.Should().Contain("receipt bytea");
        migration.Should().Contain("expires_at");
        migration.Should().Contain("16777216");
        insert.Should().Contain("honua.grpc_apply_edits_receipt");
        insert.Should().Contain("ON CONFLICT (scope_key) DO UPDATE");
        insert.Should().Contain("existing.expires_at <= now()");
        select.Should().Contain("WHERE scope_key = $1 AND expires_at > now()");
        deleteExpired.Should().Contain("DELETE FROM honua.grpc_apply_edits_receipt");
        deleteExpired.Should().Contain("WHERE expires_at <= now()");
        deleteExpired.Should().Contain("ORDER BY expires_at");
        deleteExpired.Should().Contain("LIMIT 32");
        migration.Should().Contain("ix_grpc_apply_edits_receipt_expires");
        PostgresGrpcApplyEditsReceipt.ExpiredDeleteBatchSize.Should().Be(32);
        PostgresGrpcApplyEditsReceipt.MaxReceiptBytes.Should().Be(16 * 1024 * 1024);
        PostgresGrpcApplyEditsReceipt.MaxScopeLength.Should().Be(128);
    }

    private static FeatureEditIdempotencyReceipt Receipt(Func<FeatureEditResult, ReadOnlyMemory<byte>> payload)
        => new()
        {
            Scope = "scope",
            ExpiresAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
            Payload = payload,
        };

    private sealed class RecordingTransaction : DbTransaction
    {
        public int CommitCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public bool Committed => CommitCalls > 0;

        public override IsolationLevel IsolationLevel => IsolationLevel.RepeatableRead;

        protected override DbConnection? DbConnection => null;

        public override void Commit() => CommitAsync().GetAwaiter().GetResult();

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return Task.CompletedTask;
        }

        public override void Rollback() => RollbackAsync().GetAwaiter().GetResult();

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }
    }
}
