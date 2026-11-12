// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.Assets;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class AssetTaskCoreMigrationTests : IAsyncLifetime
{
    private const string MigrationName = "153_CreateAssetTaskCore.sql";
    private const string RestrictViolation = "23001";
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";

    private static readonly Guid ProfileId = Guid.Parse("8f1c2a5e-6b3d-4c1e-9a7f-0d2b4e6c8a10");
    private static readonly Guid TaskId = Guid.Parse("2b7e9c41-5d8a-4f36-b1e0-7c3a9d5f2e84");

    private readonly PostgresFixture _postgres = new();
    private readonly string _schema = $"asset_core_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await ExecuteAsync($"CREATE SCHEMA \"{_schema}\"");
        await ApplyMigrationAsync();
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
        await _postgres.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Migration_AppliedTwice_IsANoOp()
    {
        await SeedProfileAndTaskAsync();

        await ApplyMigrationAsync();

        (await ScalarAsync<long>("SELECT count(*) FROM {s}.work_tasks")).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TaskHistory_UpdateOrDelete_IsRejected()
    {
        await SeedProfileAndTaskAsync();
        await ExecuteAsync(Qualify("""
            INSERT INTO {s}.work_task_history (event_id, task_id, command_id, action, new_version, new_status,
                actor, recorded_at, changed_fields)
            VALUES (gen_random_uuid(), @task, gen_random_uuid(), 'create', 1, 'unassigned', 'planner', now(), '{}')
            """));

        (await SqlStateAsync("UPDATE {s}.work_task_history SET actor = 'someone-else'")).Should().Be(RestrictViolation);
        (await SqlStateAsync("DELETE FROM {s}.work_task_history")).Should().Be(RestrictViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CommandReceipts_UpdateOrDelete_IsRejected()
    {
        await ExecuteAsync(Qualify("""
            INSERT INTO {s}.asset_command_receipts (service_id, command_id, subject_kind, subject_id, action, actor,
                request_sha256, status_code, response, recorded_at)
            VALUES ('county-signs', gen_random_uuid(), 'work_task', @task, 'create', 'planner', repeat('a', 64), 201, '{}', now())
            """));

        (await SqlStateAsync("UPDATE {s}.asset_command_receipts SET response = '{\"x\":1}'")).Should().Be(RestrictViolation);
        (await SqlStateAsync("DELETE FROM {s}.asset_command_receipts")).Should().Be(RestrictViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_InspectionWithoutAsset_IsRejected()
    {
        var state = await SqlStateAsync(InsertTaskSql("inspection", "unassigned", assignee: "NULL", profile: "NULL", asset: "NULL"));

        state.Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_AssignedWithoutAssignee_IsRejected()
    {
        var state = await SqlStateAsync(InsertTaskSql("repair", "assigned", assignee: "NULL", profile: "NULL", asset: "NULL"));

        state.Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_UnknownStatus_IsRejected()
    {
        var state = await SqlStateAsync(InsertTaskSql("repair", "reopened", assignee: "NULL", profile: "NULL", asset: "NULL"));

        state.Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AssetInspections_SecondForOneTask_IsRejected()
    {
        await SeedProfileAndTaskAsync();
        const string insert = """
            INSERT INTO {s}.asset_inspections (inspection_id, task_id, profile_id, asset_id, form_id, form_version,
                condition, answers, attachments, inspector, observed_at, recorded_at)
            VALUES (gen_random_uuid(), @task, @profile, 'SIGN-001', 'sign-inspection', 1, 3, '{}', '[]', 'crew', now(), now())
            """;
        await ExecuteAsync(Qualify(insert));

        (await SqlStateAsync(insert)).Should().Be(UniqueViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AssetSummaries_ConditionOutsideOneToFive_IsRejected()
    {
        await SeedProfileAndTaskAsync();

        var state = await SqlStateAsync("""
            INSERT INTO {s}.asset_summaries (profile_id, asset_id, revision, condition, updated_at)
            VALUES (@profile, 'SIGN-001', 1, 0, now())
            """);

        state.Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AssetProfiles_SecondProfileForOneLayer_IsRejected()
    {
        await SeedProfileAndTaskAsync();

        var state = await SqlStateAsync(ProfileInsertSql("gen_random_uuid()"));

        state.Should().Be(UniqueViolation);
    }

    private async Task SeedProfileAndTaskAsync()
    {
        await ExecuteAsync(Qualify(ProfileInsertSql("@profile")));
        await ExecuteAsync(Qualify(InsertTaskSql("inspection", "assigned", assignee: "'crew'", profile: "@profile", asset: "'SIGN-001'")));
    }

    private static string ProfileInsertSql(string profileId) => $$"""
        INSERT INTO {s}.asset_profiles (profile_id, service_id, layer_id, name, geometry_type, asset_id_field,
            condition_labels, state, version, created_by, created_at, updated_by, updated_at)
        VALUES ({{profileId}}, 'county-signs', 0, 'Signs', 'point', 'sign_id',
            '["Very poor","Poor","Fair","Good","Very good"]', 'active', 1, 'planner', now(), 'planner', now())
        """;

    private static string InsertTaskSql(string type, string status, string assignee, string profile, string asset) => $$"""
        INSERT INTO {s}.work_tasks (task_id, service_id, task_type, status, version, profile_id, asset_id, geometry,
            geometry_source, title, assignee, form_id, form_version, created_by, created_at, updated_at)
        VALUES (@task, 'county-signs', '{{type}}', '{{status}}', 1, {{profile}}, {{asset}},
            ST_SetSRID(ST_MakePoint(-156.47, 20.89), 4326), 'asset', 'Inspect sign', {{assignee}},
            'sign-inspection', 1, 'planner', now(), now())
        """;

    private async Task ApplyMigrationAsync()
    {
        var sql = await File.ReadAllTextAsync(MigrationPath());
        await ExecuteAsync(sql.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
    }

    private string Qualify(string sql) => sql.Replace("{s}", $"\"{_schema}\"", StringComparison.Ordinal);

    private async Task<string?> SqlStateAsync(string sql)
    {
        try
        {
            await ExecuteAsync(Qualify(sql));
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = _postgres.DataSource.CreateCommand(Qualify(sql));
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _postgres.DataSource.CreateCommand(sql);
        if (sql.Contains('@', StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("profile", ProfileId);
            command.Parameters.AddWithValue("task", TaskId);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Join(directory!.FullName, "src", "Honua.Server", "Migrations", MigrationName);
    }
}
