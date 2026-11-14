// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class AssetTaskCoreMigrationTests
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public Task InspectionEvidence_OlderRelinkerSnapshot_RejectsSerialization()
        => AssertOlderRelinkerRejectedAsync("asset_inspections");

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public Task ServiceEvidence_OlderRelinkerSnapshot_RejectsSerialization()
        => AssertOlderRelinkerRejectedAsync("asset_service_records");

    private async Task AssertOlderRelinkerRejectedAsync(string evidenceTable)
    {
        await SeedProfileAndTaskAsync();
        await using var connection = await _postgres.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var read = new NpgsqlCommand(Qualify("SELECT count(*) FROM {s}.work_tasks"), connection, transaction);
        (await read.ExecuteScalarAsync()).Should().Be(1L);

        await ExecuteAsync(Qualify(CompletionInsertSql(evidenceTable, "@profile", "'SIGN-001'")));

        await using var relink = new NpgsqlCommand(
            Qualify("UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1"), connection, transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => relink.ExecuteNonQueryAsync());
        exception.SqlState.Should().Be(PostgresErrorCodes.SerializationFailure);
        await transaction.RollbackAsync();
        (await ScalarAsync<long>("SELECT version FROM {s}.work_tasks")).Should().Be(1L);
        (await ScalarAsync<string>("SELECT asset_id FROM {s}.work_tasks")).Should().Be("SIGN-001");
        (await ScalarAsync<long>($"SELECT count(*) FROM {{s}}.{evidenceTable} WHERE asset_id = 'SIGN-001'"))
            .Should().Be(1L);
    }
}
