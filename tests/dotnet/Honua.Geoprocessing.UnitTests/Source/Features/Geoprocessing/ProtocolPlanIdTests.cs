// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class ProtocolPlanIdTests
{
    [UnitTest]
    public void Create_SameKey_NamesTheSamePlanSoAKeyedRetryKeepsItsFingerprint()
    {
        var first = ProtocolPlanId.Create("ogc-geometry-buffer", "client-retry-1");
        var retry = ProtocolPlanId.Create("ogc-geometry-buffer", "client-retry-1");

        retry.Should().Be(first);
        first.Should().MatchRegex("^ogc-geometry-buffer-[0-9a-f]{32}$");
        GeoprocessingJobService.CreateRequestFingerprint(Plan(retry))
            .Should().Be(GeoprocessingJobService.CreateRequestFingerprint(Plan(first)));
    }

    [UnitTest]
    public void Create_DifferentKeys_NameDifferentPlans()
        => ProtocolPlanId.Create("ogc-geometry-buffer", "key-a")
            .Should().NotBe(ProtocolPlanId.Create("ogc-geometry-buffer", "key-b"));

    [UnitTest]
    public void Create_WithoutKey_NamesEveryRequestAfresh()
        => ProtocolPlanId.Create("gpserver-svc-task", idempotencyKey: null)
            .Should().NotBe(ProtocolPlanId.Create("gpserver-svc-task", idempotencyKey: null));

    private static AnalysisPlan Plan(string planId)
        => new()
        {
            PlanId = planId,
            IntentId = "ogc-process:geometry.buffer",
            Steps = [new AnalysisPlanStep { StepId = "ogc-geometry-buffer", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = "geometry.buffer" }]
        };
}
