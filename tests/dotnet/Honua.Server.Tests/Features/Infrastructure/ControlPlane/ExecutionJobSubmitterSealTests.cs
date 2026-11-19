// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.SecurityState;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Honua.Server.Tests.Features.Infrastructure.ControlPlane.PostgresJobQueueTestJobs;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// The submitter snapshot on a stored execution record is trusted only with a seal made by the key ring
// for that operation; an edited, copied or unsealed snapshot leaves the job with no submitter context.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ExecutionJobSubmitterSealTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task SealedSnapshot_ReadsBack()
    {
        await using var harness = await CreateHarnessAsync();
        var store = SealingStore(harness, new EphemeralDataProtectionProvider());
        var job = WithSubmitter(CreateQueuedJob("sealed"), "reader");

        (await store.TryCreateAsync(job)).Should().BeTrue();

        (await StoredRecordTextAsync(harness, job.OperationId)).Should().Contain(DataProtectionExecutionJobSecrets.Marker);
        (await store.GetAsync(job.OperationId))!.Audit.SubmitterSecurityContext.Should().BeEquivalentTo(job.Audit.SubmitterSecurityContext);
    }

    [IntegrationTest]
    public async Task EditedSnapshot_IsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = SealingStore(harness, new EphemeralDataProtectionProvider());
        var job = WithSubmitter(CreateQueuedJob("edited"), "reader");
        (await store.TryCreateAsync(job)).Should().BeTrue();

        await harness.ExecuteAsync(
            $"UPDATE {harness.JobsTable} SET record = jsonb_set(record::jsonb, '{{audit,submitterSecurityContext,roleClaimType}}', '\"region\"')::json WHERE operation_id = @id",
            new NpgsqlParameter("id", job.OperationId));

        (await store.GetAsync(job.OperationId))!.Audit.SubmitterSecurityContext.Should().BeNull();
    }

    [IntegrationTest]
    public async Task SealedSnapshotOverRewrittenWork_IsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = SealingStore(harness, new EphemeralDataProtectionProvider());
        var job = WithSubmitter(CreateQueuedJob("rewritten-work"), "admin");
        (await store.TryCreateAsync(job)).Should().BeTrue();

        await harness.ExecuteAsync(
            $"UPDATE {harness.JobsTable} SET record = jsonb_set(record::jsonb, '{{spec,workloadName}}', '\"other-workload\"')::json WHERE operation_id = @id",
            new NpgsqlParameter("id", job.OperationId));

        (await store.GetAsync(job.OperationId))!.Audit.SubmitterSecurityContext.Should().BeNull();
    }

    [IntegrationTest]
    public async Task SnapshotCopiedFromAnotherJob_IsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = SealingStore(harness, new EphemeralDataProtectionProvider());
        var admin = WithSubmitter(CreateQueuedJob("admin-source"), "admin");
        var reader = WithSubmitter(CreateQueuedJob("reader-target"), "reader");
        (await store.TryCreateAsync(admin)).Should().BeTrue();
        (await store.TryCreateAsync(reader)).Should().BeTrue();

        await harness.ExecuteAsync(
            $"UPDATE {harness.JobsTable} AS target SET record = jsonb_set(target.record::jsonb, '{{audit}}', source.record::jsonb -> 'audit')::json "
            + $"FROM {harness.JobsTable} AS source WHERE target.operation_id = @target AND source.operation_id = @source",
            new NpgsqlParameter("target", reader.OperationId),
            new NpgsqlParameter("source", admin.OperationId));

        (await store.GetAsync(reader.OperationId))!.Audit.SubmitterSecurityContext.Should().BeNull();
    }

    [IntegrationTest]
    public async Task SnapshotWrittenWithoutSeal_IsNotTrusted()
    {
        await using var harness = await CreateHarnessAsync();
        var job = WithSubmitter(CreateQueuedJob("unsealed"), "reader");
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();

        var read = await SealingStore(harness, new EphemeralDataProtectionProvider()).GetAsync(job.OperationId);

        read!.Audit.SubmitterSecurityContext.Should().BeNull();
    }

    [IntegrationTest]
    public async Task NodeWithAnotherKeyRing_KeepsTheSnapshotItCannotVerifyWhenItRewritesTheRecord()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = new EphemeralDataProtectionProvider();
        var job = WithSubmitter(CreateQueuedJob("foreign-rewrite"), "reader");
        (await SealingStore(harness, owner).TryCreateAsync(job)).Should().BeTrue();
        var foreign = SealingStore(harness, new EphemeralDataProtectionProvider());

        var seen = (await foreign.GetAsync(job.OperationId))!;
        seen.Audit.SubmitterSecurityContext.Should().BeNull();
        (await foreign.TrySetAsync(seen with { CurrentPhase = "rewritten" })).Should().BeTrue();

        (await SealingStore(harness, owner).GetAsync(job.OperationId))!
            .Audit.SubmitterSecurityContext.Should().BeEquivalentTo(job.Audit.SubmitterSecurityContext);
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExecutionJobSubmitterSealTests));

    private static PostgresExecutionJobStore SealingStore(PostgresJobSubstrateHarness harness, IDataProtectionProvider provider)
        => new(
            new JobStoreDatabase(harness.DataSource, harness.Schema) { Secrets = new DataProtectionExecutionJobSecrets(provider) },
            Options.Create(new JobStoreOptions()),
            NullLogger<PostgresExecutionJobStore>.Instance);

    private static ExecutionJobRecord WithSubmitter(ExecutionJobRecord job, string role)
        => job with
        {
            Audit = job.Audit with
            {
                RequestedBy = $"{role}-user",
                SubmitterSecurityContext = new JobSecurityContext(
                    $"{role}-user",
                    "tenant-a",
                    [new JobSecurityClaim("role", role), new JobSecurityClaim("region", "west")],
                    "role"),
            },
        };

    private static Task<string?> StoredRecordTextAsync(PostgresJobSubstrateHarness harness, string operationId)
        => harness.ScalarAsync<string>(
            $"SELECT record::text FROM {harness.JobsTable} WHERE operation_id = @id",
            new NpgsqlParameter("id", operationId));
}
