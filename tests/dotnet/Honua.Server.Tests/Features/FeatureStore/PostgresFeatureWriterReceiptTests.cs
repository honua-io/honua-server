// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

public partial class PostgresFeatureStoreIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_ReceiptCommit_CommitsBothLayersAndReceipt()
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var receipt = TransactionReceipt();
        await using (var transaction = await store.BeginTransactionAsync())
        {
            transaction.SupportsDurableReceipts.Should().BeTrue();
            await UpdateReceiptTestAttributeAsync(transaction, (PointsLayerId, 1, "category"), "committed-points");
            await UpdateReceiptTestAttributeAsync(transaction, (PolygonsLayerId, 101, "area_name"), "committed-polygons");
            var outcome = await transaction.CommitAsync(receipt);
            outcome.Should().Be(FeatureWriterTransactionCommitOutcome.Committed);
        }

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"].Should().Be("committed-points");
        (await store.GetAsync(PolygonsLayerId, 101))!.Value.Attributes["area_name"].Should().Be("committed-polygons");
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        var stored = await receipts.TryReadAsync(receipt.Scope);
        stored.Should().NotBeNull();
        stored!.Value.ToArray().Should().Equal(receipt.Payload.ToArray());
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_DuplicateReceipt_RollsBackBothLayers()
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var receipt = TransactionReceipt();
        await using (var winner = await store.BeginTransactionAsync())
        {
            await UpdateReceiptTestAttributeAsync(winner, (PointsLayerId, 1, "category"), "winner");
            (await winner.CommitAsync(receipt)).Should().Be(FeatureWriterTransactionCommitOutcome.Committed);
        }

        var originalPolygon = (await store.GetAsync(PolygonsLayerId, 101))!.Value;
        await using (var loser = await store.BeginTransactionAsync())
        {
            await UpdateReceiptTestAttributeAsync(loser, (PointsLayerId, 1, "category"), "loser");
            await UpdateReceiptTestAttributeAsync(loser, (PolygonsLayerId, 101, "area_name"), "loser");
            (await loser.CommitAsync(receipt with { Payload = new byte[] { 9 } }))
                .Should().Be(FeatureWriterTransactionCommitOutcome.SupersededByExistingReceipt);
        }

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"].Should().Be("winner");
        (await store.GetAsync(PolygonsLayerId, 101))!.Value.Attributes["area_name"]
            .Should().Be(originalPolygon.Attributes["area_name"]);
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(receipt.Scope))!.Value.ToArray().Should().Equal(receipt.Payload.ToArray());
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_ConcurrentReceiptWinner_RollsBackLosingSnapshot()
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var receipt = TransactionReceipt();
        var original = (await store.GetAsync(PointsLayerId, 2))!.Value;
        await using var loser = await store.BeginTransactionAsync();
        await UpdateReceiptTestAttributeAsync(loser, (PointsLayerId, 2, "category"), "losing-snapshot");
        await using (var winner = await store.BeginTransactionAsync())
        {
            await UpdateReceiptTestAttributeAsync(winner, (PointsLayerId, 1, "category"), "winning-snapshot");
            (await winner.CommitAsync(receipt)).Should().Be(FeatureWriterTransactionCommitOutcome.Committed);
        }

        (await loser.CommitAsync(receipt with { Payload = new byte[] { 9 } }))
            .Should().Be(FeatureWriterTransactionCommitOutcome.SupersededByExistingReceipt);
        (await store.GetAsync(PointsLayerId, 2))!.Value.Attributes["category"]
            .Should().Be(original.Attributes["category"]);
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(receipt.Scope))!.Value.ToArray().Should().Equal(receipt.Payload.ToArray());
    }

    [IntegrationTheory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_ReceiptCommit_DistinctScopesBothCommit(IsolationLevel isolation)
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var firstReceipt = TransactionReceipt();
        var secondReceipt = TransactionReceipt();
        await using var first = await store.BeginTransactionAsync(isolation);
        await using var second = await store.BeginTransactionAsync(isolation);
        await UpdateReceiptTestAttributeAsync(first, (PointsLayerId, 1, "category"), "first-scope");
        await UpdateReceiptTestAttributeAsync(second, (PointsLayerId, 2, "category"), "second-scope");
        (await first.CommitAsync(firstReceipt)).Should().Be(FeatureWriterTransactionCommitOutcome.Committed);
        (await second.CommitAsync(secondReceipt)).Should().Be(FeatureWriterTransactionCommitOutcome.Committed);

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"].Should().Be("first-scope");
        (await store.GetAsync(PointsLayerId, 2))!.Value.Attributes["category"].Should().Be("second-scope");
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(firstReceipt.Scope))!.Value.ToArray().Should().Equal(firstReceipt.Payload.ToArray());
        (await receipts.TryReadAsync(secondReceipt.Scope))!.Value.ToArray().Should().Equal(secondReceipt.Payload.ToArray());
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_ReceiptCommit_RejectsSerializableWithoutCommitting()
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var receipt = TransactionReceipt();
        var original = (await store.GetAsync(PointsLayerId, 1))!.Value;
        await using (var transaction = await store.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            transaction.SupportsDurableReceipts.Should().BeFalse();
            await UpdateReceiptTestAttributeAsync(transaction, (PointsLayerId, 1, "category"), "unsupported");
            var commit = () => transaction.CommitAsync(receipt);
            await commit.Should().ThrowAsync<NotSupportedException>();
        }

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"]
            .Should().Be(original.Attributes["category"]);
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(receipt.Scope)).Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_LaterLayerFailure_DoesNotCommitReceiptOrEarlierLayer()
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var original = (await store.GetAsync(PointsLayerId, 1))!.Value;
        var receipt = TransactionReceipt();
        await using (var transaction = await store.BeginTransactionAsync())
        {
            await UpdateReceiptTestAttributeAsync(transaction, (PointsLayerId, 1, "category"), "rolled-back");
            var result = await transaction.ApplyEditsAsync(PolygonsLayerId,
                FeatureEditBatch.Create(operations: [FeatureEditOperation.Update(Feature.Create(long.MaxValue, null))]));
            result.IsSuccess.Should().BeFalse();
            await transaction.RollbackAsync();
        }

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"]
            .Should().Be(original.Attributes["category"]);
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(receipt.Scope)).Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("empty-payload")]
    [InlineData("oversized-payload")]
    [InlineData("blank-scope")]
    [InlineData("oversized-scope")]
    [InlineData("expired")]
    [Operation(Operations.ApplyEdits)]
    public async Task WriterTransaction_InvalidReceipt_DoesNotCommitEdits(string invalidPart)
    {
        await CreateReceiptTableAsync();
        var store = CreateFeatureStore();
        var original = (await store.GetAsync(PointsLayerId, 1))!.Value;
        var receipt = TransactionReceipt();
        receipt = invalidPart switch
        {
            "empty-payload" => receipt with { Payload = ReadOnlyMemory<byte>.Empty },
            "oversized-payload" => receipt with { Payload = new byte[16 * 1024 * 1024 + 1] },
            "blank-scope" => receipt with { Scope = " " },
            "oversized-scope" => receipt with { Scope = new string('a', 129) },
            "expired" => receipt with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            _ => throw new ArgumentException("Unknown invalid receipt part.", nameof(invalidPart))
        };
        await using (var transaction = await store.BeginTransactionAsync())
        {
            await UpdateReceiptTestAttributeAsync(transaction, (PointsLayerId, 1, "category"), "invalid-receipt");
            var commit = () => transaction.CommitAsync(receipt);
            await commit.Should().ThrowAsync<ArgumentException>();
        }

        (await store.GetAsync(PointsLayerId, 1))!.Value.Attributes["category"]
            .Should().Be(original.Attributes["category"]);
        var receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _testSchema);
        (await receipts.TryReadAsync(receipt.Scope)).Should().BeNull();
    }

    private async Task UpdateReceiptTestAttributeAsync(IFeatureWriterTransaction transaction, (int LayerId, long Id, string Field) target, string value)
    {
        var original = (await CreateFeatureStore().GetAsync(target.LayerId, target.Id))!.Value;
        var changed = Feature.Create(target.Id, original.Geometry, original.Attributes.SetItem(target.Field, value));
        var result = await transaction.ApplyEditsAsync(target.LayerId,
            FeatureEditBatch.Create(operations: [FeatureEditOperation.Update(changed)]));
        result.IsSuccess.Should().BeTrue();
    }

    private Task CreateReceiptTableAsync() => _fixture.ExecuteAsync($"""
        CREATE TABLE {_testSchema}.grpc_apply_edits_receipt (
            scope_key text PRIMARY KEY,
            receipt bytea NOT NULL,
            expires_at timestamptz NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        """);

    private static FeatureWriterTransactionReceipt TransactionReceipt() => new()
    {
        Scope = Guid.NewGuid().ToString("N"),
        Payload = new byte[] { 1, 2, 3 },
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };
}
