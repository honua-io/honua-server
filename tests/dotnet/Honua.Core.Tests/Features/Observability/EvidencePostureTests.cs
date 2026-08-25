// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Tests.Features.Observability;

public sealed class EvidencePostureTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Envelope_CompleteFreshSource_IsActionable()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.OpsFindings,
            EvidenceBackendKinds.InProcess,
            "ops-findings-evaluator",
            Now.AddSeconds(-5),
            Now.AddSeconds(-5),
            evaluatedAt: Now);

        var envelope = EvidencePosture.Envelope(Now, [source]);

        envelope.Completeness.Should().Be(EvidenceCompletenessStatuses.Complete);
        envelope.Actionable.Should().BeTrue();
        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.OpsFindings], Now).Should().BeTrue();
    }

    [Fact]
    public void Source_MissingObservationAndStaleLastSuccess_RemainsUnavailable()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.AlertDispatch,
            EvidenceBackendKinds.DurableStore,
            "alert-dispatch-store",
            observedAt: null,
            lastSuccessfulAt: Now.AddHours(-1),
            maximumObservationAgeSeconds: 60,
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Unavailable);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.MissingObservationTime);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.StaleLastSuccess);
    }

    [Fact]
    public void Source_FutureObservationAndTruncatedPage_FailsClosed()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.AlertEvents,
            EvidenceBackendKinds.DurableStore,
            "alert-event-store",
            Now.AddMinutes(5),
            Now,
            coverage: new EvidenceCoverage { HasMore = true, Truncated = true },
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Unavailable);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.FutureObservationTime);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.PageTruncated);
    }

    [Fact]
    public void Envelope_DuplicateSourceIds_IsNotActionable()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.Database,
            EvidenceBackendKinds.InProcess,
            "database-metrics",
            Now,
            Now,
            evaluatedAt: Now);

        var envelope = EvidencePosture.Envelope(Now, [source, source]);

        envelope.Actionable.Should().BeFalse();
        envelope.Completeness.Should().Be(EvidenceCompletenessStatuses.Unavailable);
        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.Database], Now).Should().BeFalse();
    }

    [Fact]
    public void Source_NotConfigured_DistinguishesConfigurationFromOutage()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.GeoprocessingQueue,
            EvidenceBackendKinds.NotConfigured,
            "execution-job-store",
            observedAt: null,
            lastSuccessfulAt: null,
            completeness: EvidenceCompletenessStatuses.NotConfigured,
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.NotConfigured);
        source.ReasonCodes.Should().Equal(EvidenceReasonCodes.SourceNotConfigured);
    }

    [Fact]
    public void Source_ConfiguredButNeverSucceeded_IsUnavailableRatherThanNotConfigured()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.DeployOperations,
            EvidenceBackendKinds.DurableStore,
            "workflow-operation-store",
            observedAt: null,
            lastSuccessfulAt: null,
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Unavailable);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.MissingObservationTime);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.NeverSucceeded);
        source.ReasonCodes.Should().NotContain(EvidenceReasonCodes.SourceNotConfigured);
    }

    [Fact]
    public void Source_IncompleteReplicaCoverage_IsPartialAndNotActionable()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.ServingLatency,
            EvidenceBackendKinds.DurableStore,
            "ops-health-rollup-store",
            Now,
            Now,
            coverage: new EvidenceCoverage { ObservedReplicaCount = 1, ExpectedReplicaCount = 2 },
            evaluatedAt: Now);
        var envelope = EvidencePosture.Envelope(Now, [source]);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Partial);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.IncompleteReplicaCoverage);
        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.ServingLatency], Now).Should().BeFalse();
    }

    [Fact]
    public void Source_MissingExpectedComponent_IsPartial()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.OperateEvents,
            EvidenceBackendKinds.Composite,
            "operate-event-feed",
            Now,
            Now,
            coverage: new EvidenceCoverage
            {
                IncludedComponentIds = ["alerts"],
                ExpectedComponentIds = ["alerts", "audit"],
            },
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Partial);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.PartialResult);
    }

    [Fact]
    public void Source_UnverifiedBackendAndInvalidWindow_IsUnavailableWithoutLeakingInput()
    {
        const string unsafeBackend = "postgres://secret.example/tenant";
        var source = EvidencePosture.Source(
            EvidenceSourceIds.AlertEvents,
            EvidenceBackendKinds.DurableStore,
            unsafeBackend,
            Now,
            Now,
            coverage: new EvidenceCoverage
            {
                RequestedFrom = Now,
                RequestedTo = Now.AddMinutes(-1),
            },
            evaluatedAt: Now);

        source.Completeness.Should().Be(EvidenceCompletenessStatuses.Unavailable);
        source.BackendId.Should().Be(EvidenceBackendKinds.Unverified);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.BackendUnverified);
        source.ReasonCodes.Should().Contain(EvidenceReasonCodes.InvalidTimeWindow);
        source.ReasonCodes.Should().NotContain(reason => reason.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void IsActionable_StaleSourceSnapshotFailsRevalidation()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.Database,
            EvidenceBackendKinds.InProcess,
            "database-pressure-signal",
            Now,
            Now,
            maximumObservationAgeSeconds: 60,
            evaluatedAt: Now);
        var envelope = EvidencePosture.Envelope(Now, [source]);

        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.Database], Now.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void IsActionable_DeserializedUnknownVocabularyFailsClosed()
    {
        var source = EvidencePosture.Source(
            EvidenceSourceIds.Database,
            EvidenceBackendKinds.InProcess,
            "database-pressure-signal",
            Now,
            Now,
            evaluatedAt: Now) with
        {
            BackendKind = "postgresql",
            ReasonCodes = ["provider-said-ok"],
        };
        var envelope = new EvidencePostureEnvelope
        {
            GeneratedAt = Now,
            Completeness = EvidenceCompletenessStatuses.Complete,
            Actionable = true,
            Sources = [source],
        };

        EvidencePosture.IsWellFormed(envelope, Now).Should().BeFalse();
        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.Database], Now).Should().BeFalse();
    }

    [Fact]
    public void IsActionable_TamperedSummaryAndNonUtcTimestampFailClosed()
    {
        var unavailable = EvidencePosture.Source(
            EvidenceSourceIds.AlertEvents,
            EvidenceBackendKinds.DurableStore,
            "alert-event-store",
            observedAt: null,
            lastSuccessfulAt: null,
            evaluatedAt: Now);
        var envelope = new EvidencePostureEnvelope
        {
            GeneratedAt = Now.ToOffset(TimeSpan.FromHours(-10)),
            Completeness = EvidenceCompletenessStatuses.Complete,
            Actionable = true,
            Sources = [unavailable],
        };

        EvidencePosture.IsActionable(envelope, [EvidenceSourceIds.AlertEvents], Now).Should().BeFalse();
    }
}
