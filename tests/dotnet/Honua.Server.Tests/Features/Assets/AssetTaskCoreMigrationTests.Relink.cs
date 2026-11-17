// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class AssetTaskCoreMigrationTests
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_VersionedRelinkWithoutEvidence_IsAllowed()
    {
        await SeedProfileAndTaskAsync();

        await ExecuteAsync(Qualify("UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1"));

        (await ScalarAsync<string>("SELECT asset_id FROM {s}.work_tasks")).Should().Be("SIGN-002");
        (await SqlStateAsync(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")))
            .Should().Be(CheckViolation);
        await ExecuteAsync(Qualify(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-002'")));
        (await SqlStateAsync("UPDATE {s}.work_tasks SET asset_id = 'SIGN-003', version = version + 1"))
            .Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_StartedTaskWithoutEvidence_AllowsVersionedRelink()
    {
        await SeedProfileAndTaskAsync();
        await ExecuteAsync(Qualify("UPDATE {s}.work_tasks SET status = 'in_progress', started_at = now()"));

        await ExecuteAsync(Qualify("UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1"));

        (await ScalarAsync<string>("SELECT asset_id FROM {s}.work_tasks")).Should().Be("SIGN-002");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_CompletedTaskWithoutRecord_RejectsRelink()
    {
        await SeedProfileAndTaskAsync();
        await ExecuteAsync(Qualify("UPDATE {s}.work_tasks SET status = 'completed', completed_at = now(), completion = '{}'"));

        (await SqlStateAsync("UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1"))
            .Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkTasks_ServiceEvidencePreventsRelink()
    {
        await SeedProfileAndTaskAsync("repair");
        await ExecuteAsync(Qualify(CompletionInsertSql("asset_service_records", "@profile", "'SIGN-001'")));

        (await SqlStateAsync("UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1"))
            .Should().Be(CheckViolation);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CompletionEvidence_InFlightInsertBlocksConcurrentRelink()
    {
        await SeedProfileAndTaskAsync();
        await using var connection = await _postgres.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand(
            Qualify(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")), connection, transaction);
        insert.Parameters.AddWithValue("profile", ProfileId);
        insert.Parameters.AddWithValue("task", TaskId);
        await insert.ExecuteNonQueryAsync();

        var relink = "UPDATE {s}.work_tasks SET asset_id = 'SIGN-002', version = version + 1";
        (await SqlStateAsync("SET lock_timeout = '250ms'; " + relink)).Should().Be("55P03");
        await transaction.CommitAsync();
        (await SqlStateAsync(relink)).Should().Be(CheckViolation);
        (await ScalarAsync<string>("SELECT asset_id FROM {s}.work_tasks")).Should().Be("SIGN-001");
    }
}
