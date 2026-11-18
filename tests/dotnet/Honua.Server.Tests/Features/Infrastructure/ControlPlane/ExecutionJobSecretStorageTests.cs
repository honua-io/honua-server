// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
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

// A job capability in a stored execution record is protected at rest and readable only by a node
// holding the key ring.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ExecutionJobSecretStorageTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Token = "4f9c2b7e1d3a5c6b8e0f2a4c6e8b0d2f4a6c8e0b2d4f6a8c0e2b4d6f8a0c2e4b";

    [IntegrationTest]
    public async Task StoredRecord_DoesNotHoldTheJobCapability_AndReadsItBack()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExecutionJobSecretStorageTests));
        var store = ProtectedStore(harness, new EphemeralDataProtectionProvider());
        var job = WithToken(CreateQueuedJob("protected-token"));

        (await store.TryCreateAsync(job)).Should().BeTrue();

        var stored = await StoredRecordTextAsync(harness, job.OperationId);
        stored.Should().NotContain(Token).And.Contain(DataProtectionExecutionJobSecrets.Marker);
        (await store.GetAsync(job.OperationId))!.Spec.Parameters[ExecutionJobSecretParameters.JobToken].Should().Be(Token);
    }

    [IntegrationTest]
    public async Task HostWithoutKeyRing_PreservesTheProtectedValueWhenItRewritesTheRecord()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExecutionJobSecretStorageTests));
        var provider = new EphemeralDataProtectionProvider();
        var job = WithToken(CreateQueuedJob("worker-rewrite"));
        (await ProtectedStore(harness, provider).TryCreateAsync(job)).Should().BeTrue();

        var worker = harness.CreateNodeJobStore();
        var seen = (await worker.GetAsync(job.OperationId))!;
        seen.Spec.Parameters[ExecutionJobSecretParameters.JobToken].Should().StartWith(DataProtectionExecutionJobSecrets.Marker);
        (await worker.TrySetAsync(seen with { CurrentPhase = "rewritten" })).Should().BeTrue();

        (await StoredRecordTextAsync(harness, job.OperationId)).Should().NotContain(Token);
        (await ProtectedStore(harness, provider).GetAsync(job.OperationId))!
            .Spec.Parameters[ExecutionJobSecretParameters.JobToken].Should().Be(Token);
    }

    [IntegrationTest]
    public async Task RecordWrittenBeforeProtection_IsReadAndProtectedOnItsNextWrite()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExecutionJobSecretStorageTests));
        var job = WithToken(CreateQueuedJob("legacy-plaintext"));
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        var store = ProtectedStore(harness, new EphemeralDataProtectionProvider());

        var read = (await store.GetAsync(job.OperationId))!;
        read.Spec.Parameters[ExecutionJobSecretParameters.JobToken].Should().Be(Token);
        (await store.TrySetAsync(read with { CurrentPhase = "upgraded" })).Should().BeTrue();

        (await StoredRecordTextAsync(harness, job.OperationId)).Should().NotContain(Token);
    }

    [IntegrationTest]
    public async Task ProtectedValueFromAnotherKeyRing_IsNeverExposedAsACapability()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExecutionJobSecretStorageTests));
        var job = WithToken(CreateQueuedJob("foreign-ring"));
        (await ProtectedStore(harness, new EphemeralDataProtectionProvider()).TryCreateAsync(job)).Should().BeTrue();

        var read = await ProtectedStore(harness, new EphemeralDataProtectionProvider()).GetAsync(job.OperationId);

        read!.Spec.Parameters[ExecutionJobSecretParameters.JobToken].Should().StartWith(DataProtectionExecutionJobSecrets.Marker);
    }

    private static PostgresExecutionJobStore ProtectedStore(PostgresJobSubstrateHarness harness, IDataProtectionProvider provider)
        => new(
            new JobStoreDatabase(harness.DataSource, harness.Schema) { Secrets = new DataProtectionExecutionJobSecrets(provider) },
            Options.Create(new JobStoreOptions()),
            NullLogger<PostgresExecutionJobStore>.Instance);

    private static ExecutionJobRecord WithToken(ExecutionJobRecord job)
        => job with
        {
            Spec = job.Spec with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobSecretParameters.JobToken] = Token,
                    ["env.HONUA_JOB_ID"] = job.OperationId,
                },
            },
        };

    private static Task<string?> StoredRecordTextAsync(PostgresJobSubstrateHarness harness, string operationId)
        => harness.ScalarAsync<string>(
            $"SELECT record::text FROM {harness.JobsTable} WHERE operation_id = @id",
            new NpgsqlParameter("id", operationId));
}
