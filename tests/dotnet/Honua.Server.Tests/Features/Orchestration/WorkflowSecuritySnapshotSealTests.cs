// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Infrastructure.SecurityState;
using Honua.Server.Features.Orchestration;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Orchestration;

// A workflow run's requester snapshot and a definition's author snapshot are trusted only with a
// seal made by the key ring for that record.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class WorkflowSecuritySnapshotSealTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly JobSecurityContext Author = new(
        "author", "tenant-a", [new JobSecurityClaim("role", "reader"), new JobSecurityClaim("region", "west")], "role");

    [IntegrationTest]
    public async Task Definition_SealedAuthor_ReadsBack()
    {
        await using var harness = await CreateHarnessAsync();
        var store = DefinitionStore(harness, new EphemeralDataProtectionProvider());
        var definition = Definition();

        await store.SetAsync(definition);

        (await store.GetAsync(definition.WorkflowId))!.AuthorSecurityContext.Should().BeEquivalentTo(Author);
    }

    [IntegrationTest]
    public async Task Definition_EditedAuthor_IsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = DefinitionStore(harness, new EphemeralDataProtectionProvider());
        var definition = Definition();
        await store.SetAsync(definition);

        await harness.ExecuteAsync(
            $"UPDATE {harness.Database.Table("workflow_definitions")} SET record = jsonb_set(record, '{{authorSecurityContext,roleClaimType}}', '\"region\"') WHERE workflow_id = @id",
            new NpgsqlParameter("id", definition.WorkflowId));

        (await store.GetAsync(definition.WorkflowId))!.AuthorSecurityContext.Should().BeNull();
    }

    [IntegrationTest]
    public async Task Definition_NodeWithAnotherKeyRing_KeepsTheAuthorItCannotVerify()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = new EphemeralDataProtectionProvider();
        var definition = Definition();
        await DefinitionStore(harness, owner).SetAsync(definition);
        var foreign = DefinitionStore(harness, new EphemeralDataProtectionProvider());

        var seen = (await foreign.GetAsync(definition.WorkflowId))!;
        seen.AuthorSecurityContext.Should().BeNull();
        await foreign.SetAsync(seen with { Name = "renamed" });

        (await DefinitionStore(harness, owner).GetAsync(definition.WorkflowId))!.AuthorSecurityContext.Should().BeEquivalentTo(Author);
    }

    [IntegrationTest]
    public async Task Run_SealedRequester_ReadsBack_AndACopyToAnotherRunIsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = RunStore(harness, new EphemeralDataProtectionProvider());
        var sealedRun = Run();
        var other = Run();
        (await store.TryCreateAsync(sealedRun)).Should().BeTrue();
        (await store.TryCreateAsync(other with { Audit = new() })).Should().BeTrue();

        await harness.ExecuteAsync(
            $"UPDATE {harness.Database.Table("workflow_runs")} AS target SET record = jsonb_set(target.record, '{{audit}}', source.record -> 'audit') "
            + $"FROM {harness.Database.Table("workflow_runs")} AS source WHERE target.run_id = @target AND source.run_id = @source",
            new NpgsqlParameter("target", other.RunId),
            new NpgsqlParameter("source", sealedRun.RunId));

        (await store.GetAsync(sealedRun.RunId))!.Audit.SubmitterSecurityContext.Should().BeEquivalentTo(Author);
        (await store.GetAsync(other.RunId))!.Audit.SubmitterSecurityContext.Should().BeNull();
    }

    private async Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
    {
        var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(WorkflowSecuritySnapshotSealTests));
        using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream("Honua.Server.Migrations.167_CreateWorkflowStores.sql")!;
        using var reader = new StreamReader(stream);
        var ddl = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{harness.Schema}\"", StringComparison.Ordinal);
        await database.ExecuteDdlUnderLockAsync(ddl);
        return harness;
    }

    private static ControlPlaneStorage Storage(PostgresJobSubstrateHarness harness, IDataProtectionProvider keyRing)
        => new(
            new JobStoreDatabase(harness.DataSource, harness.Schema) { Secrets = new DataProtectionExecutionJobSecrets(keyRing) },
            Substitute.For<ILeaseStore>());

    private static PostgresWorkflowDefinitionStore DefinitionStore(PostgresJobSubstrateHarness harness, IDataProtectionProvider keyRing)
        => new(Storage(harness, keyRing));

    private static PostgresWorkflowRunStore RunStore(PostgresJobSubstrateHarness harness, IDataProtectionProvider keyRing)
        => new(Storage(harness, keyRing), Options.Create(new ControlPlaneRetentionOptions()));

    private static WorkflowDefinition Definition() => new()
    {
        WorkflowId = Guid.NewGuid().ToString("N"),
        Name = "Sealed workflow",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Steps = [new WorkflowStepDefinition { StepId = "extract", Plan = new AnalysisPlan { PlanId = "plan", IntentId = "intent", Steps = [] } }],
        AuthorSecurityContext = Author,
    };

    private static WorkflowRun Run() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        WorkflowId = Guid.NewGuid().ToString("N"),
        Status = WorkflowRunStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        StepStates = [new WorkflowStepState { StepId = "extract", PlanId = "plan", Status = WorkflowStepStatus.Running }],
        Audit = new() { RequestedBy = "author", SubmitterSecurityContext = Author },
    };
}
