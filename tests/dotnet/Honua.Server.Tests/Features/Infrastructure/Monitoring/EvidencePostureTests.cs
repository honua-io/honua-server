// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Contract tests for the shared <c>evidencePosture</c> envelope (#3475): the closed vocabularies,
/// the fail-closed validation rules, and the source-generated serialization the REST and MCP
/// projections share.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class EvidencePostureTests
{
    private const string BackendId = "test-backend";
    private const string SourceId = EvidencePostureVocabulary.SourceIds.Findings;
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(5);

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_CompleteFreshSource_IsActionableAndCarriesValidityWindow()
    {
        var now = DateTimeOffset.UtcNow;

        var posture = EvidencePostureFactory.Build(
            now,
            EvidencePostureFactory.Complete(
                SourceId, EvidencePostureVocabulary.BackendKinds.DurableStore, BackendId, now, Validity));

        var source = Assert.Single(posture.Sources);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, posture.Status);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, source.Completeness);
        Assert.Empty(source.ReasonCodes);
        Assert.Equal((long)Validity.TotalSeconds, source.MaximumAgeSeconds);
        Assert.Equal(now.ToUniversalTime().Add(Validity), source.ValidUntil);
        Assert.True(EvidencePostureFactory.IsActionable(source));
        Assert.Equal(EvidencePosture.CurrentSchemaVersion, posture.SchemaVersion);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NeverSucceededSource_ReportsUnavailableAndKeepsTimestampsMissing()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Unavailable(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                EvidencePostureVocabulary.ReasonCodes.NeverSucceeded,
                maximumAge: Validity),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded, source.ReasonCodes);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.MissingObservationTime, source.ReasonCodes);

        // A missing timestamp is never backfilled with the evaluation time.
        Assert.Null(source.ObservedAt);
        Assert.Null(source.LastSuccessfulAt);
        Assert.False(EvidencePostureFactory.IsActionable(source));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_StaleObservation_FailsClosedOnValidityWindow()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                now.AddHours(-1),
                Validity),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.Stale, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_FutureObservation_FailsClosed()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                now.AddHours(1),
                Validity),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.FutureObservationTime, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_FutureLastSuccessfulAt_FailsClosedEvenWithCurrentObservation()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            new EvidenceSourceEnvelope
            {
                SourceId = SourceId,
                BackendKind = EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId = BackendId,
                ObservedAt = now,
                LastSuccessfulAt = now.AddDays(1),
                Completeness = EvidencePostureVocabulary.Completeness.Complete,
            },
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.FutureObservationTime, source.ReasonCodes);
        Assert.False(EvidencePostureFactory.IsActionable(source));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_ObservationNewerThanLastSuccess_ReportsMalformedTime()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            new EvidenceSourceEnvelope
            {
                SourceId = SourceId,
                BackendKind = EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId = BackendId,
                ObservedAt = now,
                LastSuccessfulAt = now.AddMinutes(-30),
                Completeness = EvidencePostureVocabulary.Completeness.Complete,
            },
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.MalformedObservationTime, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_InvertedRequestedWindow_ReportsMalformedTime()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                now,
                Validity,
                new EvidenceSourceCoverage { RequestedFrom = now, RequestedTo = now.AddMinutes(-10) }),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.MalformedObservationTime, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_UnverifiedBackend_FailsClosedWithoutLeakingBackendDetail()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            new EvidenceSourceEnvelope
            {
                SourceId = SourceId,
                BackendKind = EvidencePostureVocabulary.BackendKinds.Unverified,
                BackendId = null,
                ObservedAt = now,
                LastSuccessfulAt = now,
                Completeness = EvidencePostureVocabulary.Completeness.Complete,
            },
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.BackendUnverified, source.ReasonCodes);
        Assert.False(EvidencePostureFactory.IsActionable(source));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_TruncatedPage_StaysPartialRatherThanUnavailable()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                now,
                Validity,
                new EvidenceSourceCoverage { Page = 1, PageSize = 50, HasMore = true, Truncated = true }),
            now);

        // Valid partial data must not be misreported as "the backend supplied nothing".
        Assert.Equal(EvidencePostureVocabulary.Completeness.Partial, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.Truncated, source.ReasonCodes);
        Assert.False(EvidencePostureFactory.IsActionable(source));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_PartialMultiSourceResult_StaysPartial()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Partial(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.Composite,
                BackendId,
                now,
                Validity,
                EvidencePostureVocabulary.ReasonCodes.PartialResult),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Partial, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.PartialResult, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_IncompleteReplicaCoverage_ReportsIncompleteCoverage()
    {
        var now = DateTimeOffset.UtcNow;

        var source = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                now,
                Validity,
                new EvidenceSourceCoverage { ObservedReplicaCount = 1, ExpectedReplicaCount = 3 }),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Partial, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NotConfiguredSource_StaysDistinctFromUnavailable()
    {
        var now = DateTimeOffset.UtcNow;

        var notConfigured = EvidencePostureFactory.Validate(
            EvidencePostureFactory.NotConfigured(
                SourceId, EvidencePostureVocabulary.BackendKinds.DurableStore, BackendId),
            now);
        var unavailable = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Unavailable(
                SourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                EvidencePostureVocabulary.ReasonCodes.SourceUnavailable),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.NotConfigured, notConfigured.Completeness);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, unavailable.Completeness);
        Assert.NotEqual(notConfigured.Completeness, unavailable.Completeness);
        Assert.False(EvidencePostureFactory.IsActionable(notConfigured));
        Assert.False(EvidencePostureFactory.IsActionable(unavailable));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Aggregate_MissingComponent_ReportsIncompleteComponentCoverage()
    {
        var now = DateTimeOffset.UtcNow;
        var healthy = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                "test.alpha", EvidencePostureVocabulary.BackendKinds.InProcess, BackendId, now, Validity),
            now);
        var broken = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Unavailable(
                "test.beta",
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                BackendId,
                EvidencePostureVocabulary.ReasonCodes.SourceUnavailable),
            now);

        var posture = EvidencePostureFactory.Build(
            now,
            EvidencePostureFactory.Aggregate("test", BackendId, Validity, [healthy, broken]),
            healthy,
            broken);

        var aggregate = posture.Sources.Single(source => source.SourceId == "test");
        Assert.Equal(EvidencePostureVocabulary.Completeness.Partial, aggregate.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage, aggregate.ReasonCodes);
        Assert.Equal(["test.alpha"], aggregate.Coverage!.IncludedComponentIds);
        Assert.Equal(["test.alpha", "test.beta"], aggregate.Coverage.ExpectedComponentIds);

        // The summary never hides the individual states.
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, posture.Status);
        Assert.Equal(3, posture.Sources.Count);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Aggregate_AllComponentsHealthy_IsCompleteAtOldestComponentObservation()
    {
        var now = DateTimeOffset.UtcNow;
        var older = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                "test.alpha",
                EvidencePostureVocabulary.BackendKinds.InProcess,
                BackendId,
                now.AddMinutes(-2),
                Validity),
            now);
        var newer = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Complete(
                "test.beta", EvidencePostureVocabulary.BackendKinds.InProcess, BackendId, now, Validity),
            now);

        var aggregate = EvidencePostureFactory.Validate(
            EvidencePostureFactory.Aggregate("test", BackendId, Validity, [older, newer]),
            now);

        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, aggregate.Completeness);
        Assert.Equal(EvidencePostureVocabulary.BackendKinds.Composite, aggregate.BackendKind);
        Assert.Equal(older.ObservedAt, aggregate.ObservedAt);
        Assert.True(EvidencePostureFactory.IsActionable(aggregate));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Serialize_UsesSourceGeneratedContextAndClosedVocabularyWireNames()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var response = new OpsFindingsListResponse
        {
            GeneratedAt = now,
            EvidencePosture = EvidencePostureFactory.Build(
                now,
                EvidencePostureFactory.Complete(
                    SourceId,
                    EvidencePostureVocabulary.BackendKinds.DurableStore,
                    BackendId,
                    now,
                    Validity,
                    new EvidenceSourceCoverage { Page = 1, PageSize = 50, HasMore = false })),
            Findings = [],
        };

        var json = JsonSerializer.Serialize(response, OpsObservabilityJsonContext.Default.OpsFindingsListResponse);

        using var document = JsonDocument.Parse(json);
        var posture = document.RootElement.GetProperty("evidencePosture");
        Assert.Equal(EvidencePosture.CurrentSchemaVersion, posture.GetProperty("schemaVersion").GetString());
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, posture.GetProperty("status").GetString());

        var source = posture.GetProperty("sources")[0];
        Assert.Equal(SourceId, source.GetProperty("sourceId").GetString());
        Assert.Equal(
            EvidencePostureVocabulary.BackendKinds.DurableStore,
            source.GetProperty("backendKind").GetString());
        Assert.Equal(BackendId, source.GetProperty("backendId").GetString());
        Assert.Equal(now, source.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(now, source.GetProperty("lastSuccessfulAt").GetDateTimeOffset());
        Assert.Equal((long)Validity.TotalSeconds, source.GetProperty("maximumAgeSeconds").GetInt64());
        Assert.Equal(50, source.GetProperty("coverage").GetProperty("pageSize").GetInt32());

        // generatedAt stays response/evaluation time and is serialized separately.
        Assert.Equal(now, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
    }
}
