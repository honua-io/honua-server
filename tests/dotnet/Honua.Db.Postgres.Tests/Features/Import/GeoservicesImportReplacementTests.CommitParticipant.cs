// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportReplacementTests
{
    [Fact]
    public async Task ImportLayerAsync_CommitParticipantRejectsReplacement_PreservesPriorTarget()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportCommitRejected");
        try
        {
            await SeedPriorTargetAsync(schema);
            using var handler = new ReplacementFeatureServerHandler(["CCC"]);
            var service = CreateService(handler, schema);
            var request = BuildRequest(schema, overwrite: true) with
            {
                CommitParticipant = (_, _) => throw new InvalidOperationException("Execution lease lost")
            };

            var result = await service.ImportLayerAsync(request);

            result.Success.Should().BeFalse();
            result.DataCommitted.Should().BeFalse();
            (await ReadCodesAsync(schema)).Should().Equal("AAA", "BBB");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_CommitParticipantWritesReceipt_CommitsReceiptWithReplacement()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportCommitReceipt");
        try
        {
            await SeedPriorTargetAsync(schema);
            await CreateReceiptTableAsync(schema);

            using var handler = new ReplacementFeatureServerHandler(["CCC"]);
            var service = CreateService(handler, schema);
            var request = BuildRequest(schema, overwrite: true) with
            {
                CommitParticipant = PersistReceiptAsync
            };

            var result = await service.ImportLayerAsync(request);

            result.Success.Should().BeTrue(result.ErrorMessage);
            (await ReadCodesAsync(schema)).Should().Equal("CCC");
            await using var reader = await fixture.DataSource.OpenConnectionAsync();
            await using var receipt = new NpgsqlCommand(
                $"SELECT feature_count FROM \"{schema}\".import_receipts", reader);
            (await receipt.ExecuteScalarAsync()).Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_CommitParticipantFailsAfterReceipt_RollsBackReceiptAndReplacement()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportCommitRollback");
        try
        {
            await SeedPriorTargetAsync(schema);
            await CreateReceiptTableAsync(schema);

            using var handler = new ReplacementFeatureServerHandler(["CCC"]);
            var service = CreateService(handler, schema);
            var request = BuildRequest(schema, overwrite: true) with
            {
                CommitParticipant = async (scope, cancellationToken) =>
                {
                    await PersistReceiptAsync(scope, cancellationToken);
                    throw new InvalidOperationException("Execution lease lost");
                }
            };

            var result = await service.ImportLayerAsync(request);

            result.Success.Should().BeFalse();
            result.DataCommitted.Should().BeFalse();
            (await ReadCodesAsync(schema)).Should().Equal("AAA", "BBB");
            await using var reader = await fixture.DataSource.OpenConnectionAsync();
            await using var receipt = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{schema}\".import_receipts", reader);
            (await receipt.ExecuteScalarAsync()).Should().Be(0L);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task CreateReceiptTableAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            $"CREATE TABLE \"{schema}\".import_receipts (feature_count integer NOT NULL)", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static async ValueTask PersistReceiptAsync(ImportCommitScope scope, CancellationToken cancellationToken)
    {
        scope.PhysicalTable.Should().Be(TableName);
        var transaction = scope.Transaction.Should().BeOfType<NpgsqlTransaction>().Subject;
        await using var receipt = new NpgsqlCommand(
            $"INSERT INTO \"{scope.Schema}\".import_receipts VALUES (@count)", transaction.Connection, transaction);
        receipt.Parameters.AddWithValue("count", scope.FeatureCount);
        await receipt.ExecuteNonQueryAsync(cancellationToken);
    }
}
