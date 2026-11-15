// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresRowSecurityWriteCheckIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false, false, "RAISE EXCEPTION 'commit-failure' USING ERRCODE = '23514';")]
    [InlineData(false, true, "PERFORM pg_terminate_backend(pg_backend_pid());")]
    [InlineData(true, false, "RAISE EXCEPTION 'commit-failure' USING ERRCODE = '23514';")]
    [InlineData(true, true, "PERFORM pg_terminate_backend(pg_backend_pid());")]
    public async Task ApplyEdits_ReplicaRowCommitFailure_PreservesCommitUncertainty(bool ordered, bool expectedUnknownOutcome, string failureSql)
    {
        await InstallDeferredCommitFailureAsync(failureSql);
        using var origin = ReplicaUploadOriginScope.Begin("commit-outcome-test");
        var feature = Row(0, "north", "commit-failure");
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Create(feature)])
            : FeatureEditBatch.Create(creates: [feature]);

        var result = await _store.ApplyEditsAsync(LayerId, batch);

        var operation = result.CreateResults.Should().ContainSingle().Subject;
        operation.IsSuccess.Should().BeFalse();
        operation.IsCommitOutcomeUnknown.Should().Be(expectedUnknownOutcome,
            "a terminal response during COMMIT cannot establish whether the edit committed");
        operation.ErrorMessage.Should().NotContain("commit-failure");
        (await CountRowsAsync("north")).Should().Be(1);
    }

    private Task InstallDeferredCommitFailureAsync(string failureSql)
    {
        return fixture.ExecuteAsync($$"""
            CREATE FUNCTION "{{_schema}}".reject_commit() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                {{failureSql}}
                RETURN NEW;
            END;
            $body$;
            CREATE CONSTRAINT TRIGGER reject_commit
                AFTER INSERT ON "{{_schema}}".features
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION "{{_schema}}".reject_commit();
            """);
    }
}
