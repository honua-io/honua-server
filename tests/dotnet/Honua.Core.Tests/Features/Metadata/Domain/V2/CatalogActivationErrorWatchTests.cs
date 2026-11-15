// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class CatalogActivationErrorWatchTests
{
    private static readonly DateTimeOffset Activation = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly CatalogActivationEvidence Evidence = new(Activation, TimeSpan.FromMinutes(5));

    [UnitTest]
    public void Evaluate_FreshSustainedRegression_ReturnsReason()
    {
        var reason = Evaluate([Row(-2, 100, 1), Row(6, 100, 30), Row(7, 100, 35)]);
        reason.Should().Contain("OGC");
    }

    [UnitTest]
    public void Evaluate_OverlappingSmallWindows_DoesNotSumRequestsOrErrors()
    {
        Evaluate([Row(-2, 100, 0), Row(6, 10, 4), Row(7, 10, 4)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_WindowContainsPreActivationTraffic_DoesNotRollBack()
    {
        CatalogActivationErrorWatch.Evaluate(Evidence, Activation.AddMinutes(4),
            [Row(-2, 100, 0), Row(2, 100, 40), Row(3, 100, 40)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_BaselineAlreadyBad_DoesNotRollBack()
    {
        Evaluate([Row(-2, 100, 30), Row(6, 100, 35), Row(7, 100, 40)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_SingleBadSampleOrRecoveredSample_DoesNotRollBack()
    {
        Evaluate([Row(-2, 100, 0), Row(7, 100, 40)]).Should().BeNull();
        Evaluate([Row(-2, 100, 0), Row(6, 100, 40), Row(7, 100, 0)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_StaleEvidenceOrMissingBaseline_DoesNotRollBack()
    {
        Evaluate([Row(-7, 100, 0), Row(6, 100, 40), Row(7, 100, 40)]).Should().BeNull();
        CatalogActivationErrorWatch.Evaluate(Evidence, Activation.AddMinutes(12),
            [Row(-2, 100, 0), Row(6, 100, 40), Row(7, 100, 40)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_NewReplica_DoesNotAttributeUnmatchedTrafficToChange()
    {
        Evaluate([Row(-2, 100, 0), Row(6, 100, 40) with { ReplicaId = "new" }, Row(7, 100, 40) with { ReplicaId = "new" }]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_HealthyProtocol_DoesNotHideBrokenProtocol()
    {
        Evaluate([Row(-2, 100, 0), Row(6, 100, 40), Row(7, 100, 40),
            Row(7, 10000, 0) with { Point = Row(7, 10000, 0).Point with { Protocol = "Tiles" } }])
            .Should().NotBeNull();
    }

    [UnitTest]
    public void Evaluate_DuplicateBuckets_DoNotCreateSustainedEvidence()
    {
        Evaluate([Row(-2, 100, 0), Row(7, 100, 40), Row(7, 100, 40)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_SustainedLatencyRegression_ReturnsReason()
    {
        Evaluate([Latency(-2, 300), Latency(6, 2500), Latency(7, 2600)])
            .Should().Contain("latency");
    }

    [UnitTest]
    public void Evaluate_AlreadySlowOrRecoveredLatency_DoesNotRollBack()
    {
        Evaluate([Latency(-2, 2300), Latency(6, 2500), Latency(7, 2600)]).Should().BeNull();
        Evaluate([Latency(-2, 300), Latency(6, 2500), Latency(7, 300)]).Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_DifferentRegressionInEachBucket_DoesNotRollBack()
    {
        Evaluate([Row(-2, 100, 0), Latency(6, 2500), Row(7, 100, 40)]).Should().BeNull();
    }

    private static OpsHealthLatencyRow Latency(int minute, double p95) => Row(minute, 100, 0) with
    {
        Point = Row(minute, 100, 0).Point with { P95Ms = p95 },
    };

    private static string? Evaluate(IReadOnlyList<OpsHealthLatencyRow> rows) =>
        CatalogActivationErrorWatch.Evaluate(Evidence, Activation.AddMinutes(8), rows);

    private static OpsHealthLatencyRow Row(int minute, long requests, long errors) => new()
    {
        ReplicaId = "host-a",
        BucketStart = Activation.AddMinutes(minute),
        Point = new() { Protocol = "OGC", RequestCount = requests, ErrorCount = errors, P50Ms = 1, P95Ms = 1, P99Ms = 1, MaxMs = 1 },
    };
}
