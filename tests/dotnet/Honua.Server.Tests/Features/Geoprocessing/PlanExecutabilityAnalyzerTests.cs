// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for <see cref="PlanExecutabilityAnalyzer"/>, the shared component that makes the
/// single-step direct-execution reality of a plan honest for validate/dry-run surfaces (#2806).
/// </summary>
public sealed class PlanExecutabilityAnalyzerTests
{
    // Everything except the two sync-only ids is dispatchable in these tests.
    private static bool Dispatchable(string processId)
        => processId is not ("analytics.cluster" or "analytics.density");

    [UnitTest]
    public void AnalyzeDirectExecution_SingleDispatchableGeoprocessStep_HasNoAdvisories()
    {
        var plan = Plan(
            Step("s1", AnalysisPlanStepKind.Geoprocess, "geometry.buffer"));

        var warnings = PlanExecutabilityAnalyzer.AnalyzeDirectExecution(plan, Dispatchable);

        warnings.Should().BeEmpty();
    }

    [UnitTest]
    public void AnalyzeDirectExecution_MultipleProcessSteps_WarnsRemainingProcessesDropped()
    {
        var plan = Plan(
            Step("s1", AnalysisPlanStepKind.Geoprocess, "geometry.buffer"),
            Step("s2", AnalysisPlanStepKind.Geoprocess, "analytics.spatial-join"));

        var warnings = PlanExecutabilityAnalyzer.AnalyzeDirectExecution(plan, Dispatchable);

        warnings.Should().Contain(w => w.Contains("silently dropped"));
    }

    [UnitTest]
    public void AnalyzeDirectExecution_QueryThenGeoprocess_WarnsProcessStepNotFirstAndIgnoredKind()
    {
        // A natural "query then buffer" plan: the buffer step lands at index 1, but the executor
        // reads inputs from step.0, so direct execution fails. The query step is also ignored.
        var plan = Plan(
            Step("query", AnalysisPlanStepKind.QueryFeatures, processId: null),
            Step("buffer", AnalysisPlanStepKind.Geoprocess, "geometry.buffer"));

        var warnings = PlanExecutabilityAnalyzer.AnalyzeDirectExecution(plan, Dispatchable);

        warnings.Should().Contain(w => w.Contains("position 1"));
        warnings.Should().Contain(w => w.Contains("QueryFeatures"));
    }

    [UnitTest]
    public void AnalyzeDirectExecution_SyncOnlyProcessId_WarnsNoJobExecutor()
    {
        var plan = Plan(
            Step("s1", AnalysisPlanStepKind.Geoprocess, "analytics.cluster"));

        var warnings = PlanExecutabilityAnalyzer.AnalyzeDirectExecution(plan, Dispatchable);

        warnings.Should().Contain(w => w.Contains("no job executor"));
    }

    [UnitTest]
    public void AnalyzeDirectExecution_NullDispatchabilityOracle_SkipsSyncOnlyAdvisory()
    {
        var plan = Plan(
            Step("s1", AnalysisPlanStepKind.Geoprocess, "analytics.cluster"));

        var warnings = PlanExecutabilityAnalyzer.AnalyzeDirectExecution(plan, isProcessDispatchable: null);

        warnings.Should().NotContain(w => w.Contains("no job executor"));
    }

    private static AnalysisPlan Plan(params AnalysisPlanStep[] steps) => new()
    {
        PlanId = "plan-1",
        IntentId = "intent-1",
        Steps = steps
    };

    private static AnalysisPlanStep Step(string stepId, AnalysisPlanStepKind kind, string? processId) => new()
    {
        StepId = stepId,
        Kind = kind,
        ProcessId = processId
    };
}
