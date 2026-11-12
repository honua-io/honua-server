// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Assets;

// Shared setup for the asset and task tests: database reset and counts, fixture assets, a published form and a profile.
internal static class AssetTestData
{
    // TRUNCATE bypasses the append-only row triggers, which is what a test reset needs.
    public static async Task ResetAsync(WebAppFixture fixture)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            TRUNCATE honua.asset_inspections, honua.asset_service_records, honua.work_task_history,
                honua.asset_command_receipts, honua.asset_summaries, honua.work_tasks, honua.asset_profiles
            """;
        await command.ExecuteNonQueryAsync();
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
