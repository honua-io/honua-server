// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Assets;

// Shared setup for the asset and task tests: database reset and counts, fixture assets, a published form and a profile.
internal static class AssetTestData
{
    // The database collection serializes these shared tables. Only fixture cleanup
    // suspends user triggers, within a transaction so failure restores every guard.
    public static async Task ResetAsync(WebAppFixture fixture)
    {
        string[] tables = ["asset_inspections", "asset_service_records", "work_task_history",
            "asset_command_receipts", "asset_summaries", "work_tasks", "asset_profiles", "work_task_attachments"];
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var table in tables)
        {
            command.CommandText = $"ALTER TABLE honua.{table} DISABLE TRIGGER USER";
            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = $"TRUNCATE {string.Join(", ", tables.Select(table => $"honua.{table}"))}";
        await command.ExecuteNonQueryAsync();
        foreach (var table in tables)
        {
            command.CommandText = $"ALTER TABLE honua.{table} ENABLE TRIGGER USER";
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    public static async Task<long> CountAsync(WebAppFixture fixture, string table)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM honua.{table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
