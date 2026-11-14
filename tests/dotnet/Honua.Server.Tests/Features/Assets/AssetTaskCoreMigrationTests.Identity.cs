// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class AssetTaskCoreMigrationTests
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TaskIdentifiers_AreStableBeforeAndAfterCompletion()
    {
        await SeedProfileAndTaskAsync();
        var originalId = await ScalarAsync<long>("SELECT object_id FROM {s}.work_tasks");
        (await SqlStateAsync("UPDATE {s}.work_tasks SET object_id = DEFAULT")).Should().Be(CheckViolation);
        (await SqlStateAsync("UPDATE {s}.work_tasks SET task_id = gen_random_uuid()"))
            .Should().Be(CheckViolation);
        await ExecuteAsync(Qualify(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")));
        (await SqlStateAsync("UPDATE {s}.work_tasks SET object_id = DEFAULT")).Should().Be(CheckViolation);
        (await ScalarAsync<long>("SELECT object_id FROM {s}.work_tasks")).Should().Be(originalId);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task InspectionTask_RejectsServiceCompletionAndPinsTypeAfterInspection()
    {
        await SeedProfileAndTaskAsync();
        (await SqlStateAsync(CompletionInsertSql("asset_service_records", "@profile", "'SIGN-001'")))
            .Should().Be(CheckViolation);
        await ExecuteAsync(Qualify(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")));
        (await SqlStateAsync("UPDATE {s}.work_tasks SET task_type = 'repair', version = version + 1"))
            .Should().Be(CheckViolation);
        (await SqlStateAsync(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")))
            .Should().Be(UniqueViolation);
        (await ScalarAsync<long>("SELECT count(*) FROM {s}.asset_service_records")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ServiceTask_RejectsInspectionAndPinsTypeAfterServiceCompletion()
    {
        await SeedProfileAndTaskAsync("repair");
        (await SqlStateAsync(CompletionInsertSql("asset_inspections", "@profile", "'SIGN-001'")))
            .Should().Be(CheckViolation);
        await ExecuteAsync(Qualify(CompletionInsertSql("asset_service_records", "@profile", "'SIGN-001'")));
        (await SqlStateAsync("UPDATE {s}.work_tasks SET task_type = 'inspection', version = version + 1"))
            .Should().Be(CheckViolation);
        (await SqlStateAsync(CompletionInsertSql("asset_service_records", "@profile", "'SIGN-001'")))
            .Should().Be(UniqueViolation);
        (await ScalarAsync<long>("SELECT count(*) FROM {s}.asset_inspections")).Should().Be(0);
    }
}
