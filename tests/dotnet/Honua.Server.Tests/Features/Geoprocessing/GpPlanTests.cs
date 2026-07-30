// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit coverage for the headless <c>honua gp plan</c> dry-run path (GP Devkit
/// P5, issue #2126): proves the planner validates a process's params against the
/// typed catalog schema and the shared DAG structural gate WITHOUT executing,
/// and that the size/cost estimate warns before submit when an output is likely
/// to blow the <see cref="GeoprocessingExecutorOptions.MaxArtifactBytes"/> cap.
/// Fully offline — no Redis, no job store, no executor invocation.
/// </summary>
public sealed class GpPlanTests
{
    private const long DefaultCap = 50L * 1024L * 1024L;

    // POINT(0 0) — the same WKB the durable-runtime buffer tests use.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static BuiltInProcessCatalog Catalog() => new();

    [UnitTest]
    public void Build_ValidBufferParams_ProducesValidPlanWithResolvedParamsAndOutputs()
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
        };

        var plan = GpPlanner.Build("geometry.buffer", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan.Should().NotBeNull();
        plan!.IsValid.Should().BeTrue(because: string.Join("; ", plan.Errors));
        plan.Errors.Should().BeEmpty();
        plan.ProcessId.Should().Be("geometry.buffer");
        plan.RuntimeProfile.Should().Be(RuntimeProfiles.Managed);
        plan.Outputs.Should().Contain(ArtifactKind.FeatureLayer);

        // The plan previews the single ordered step with all resolved params, and
        // fills the optional 'geodesic' flag from its catalog default.
        plan.Steps.Should().ContainSingle();
        var step = plan.Steps[0];
        step.ProcessId.Should().Be("geometry.buffer");
        step.DependsOn.Should().BeEmpty();

        var geodesic = step.Parameters.Single(p => p.Name == "geodesic");
        geodesic.Source.Should().Be(GpParamSource.Default);
        geodesic.DisplayValue.Should().Be("false");

        var distance = step.Parameters.Single(p => p.Name == "distance");
        distance.Source.Should().Be(GpParamSource.Caller);
        distance.DisplayValue.Should().Be("10");
    }

    [UnitTest]
    public void Build_UnknownProcessId_ReturnsNull()
    {
        var plan = GpPlanner.Build(
            "geometry.does-not-exist",
            new Dictionary<string, string>(StringComparer.Ordinal),
            Catalog(),
            DefaultCap,
            callerInputBytes: 0);

        plan.Should().BeNull();
    }

    [UnitTest]
    public void Build_MissingAndInvalidParams_ReportsValidationErrorsWithoutExecuting()
    {
        // Omit required 'distance'; supply a non-numeric 'srid'. Both must surface
        // as catalog validation errors — the same ProcessPlanValidator the durable
        // submit path runs — and the plan must be marked invalid.
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "not-a-number",
        };

        var plan = GpPlanner.Build("geometry.buffer", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan.Should().NotBeNull();
        plan!.IsValid.Should().BeFalse();
        plan.Errors.Should().Contain(e => e.Contains("MISSING_REQUIRED_PARAMETER") && e.Contains("distance"));
        plan.Errors.Should().Contain(e => e.Contains("INVALID_PARAMETER_VALUE") && e.Contains("srid"));
    }

    [UnitTest]
    public void Build_UnknownParameter_IsRejected()
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
            ["bogus"] = "x",
        };

        var plan = GpPlanner.Build("geometry.buffer", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan!.IsValid.Should().BeFalse();
        plan.Errors.Should().Contain(e => e.Contains("UNKNOWN_PARAMETER") && e.Contains("bogus"));
    }

    [UnitTest]
    public void Build_EnrichmentWithGateOwnedLayerPin_IsAccepted()
    {
        // The submit-time layer gate stamps authorizedDatasetLayerId onto the step and authoring
        // surfaces persist it with the plan, so a scheduled workflow's stored plan carries it.
        // Catalog validation runs BEFORE the gate, so treating this server-owned input as an
        // unknown parameter rejected every correctly pinned workflow step (#3043 review).
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["layerId"] = "7",
            ["datasetId"] = "demographics",
            ["authorizedDatasetLayerId"] = "42",
        };

        var plan = GpPlanner.Build("enrichment.enrich", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan.Should().NotBeNull();
        plan!.Errors.Should().NotContain(
            e => e.Contains("UNKNOWN_PARAMETER") && e.Contains("authorizedDatasetLayerId"),
            "the gate-owned pin is server-written, not a caller parameter");
    }

    [UnitTest]
    public void Build_EnrichmentMethodWithLegacyPredicate_IsAccepted()
    {
        // EnrichmentJobExecutor.BuildPlan reads 'predicate' only when 'method' is absent, so
        // 'method' takes precedence per the published contract. Validating 'predicate'
        // unconditionally rejected plans the executor runs happily, and made an async submission
        // fail where the equivalent synchronous POST /api/enrich succeeded (#3043 review).
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["layerId"] = "7",
            ["datasetId"] = "demographics",
            ["method"] = "point-in-polygon",
            ["predicate"] = "legacy-ignored-value",
        };

        var plan = GpPlanner.Build("enrichment.enrich", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan.Should().NotBeNull();
        plan!.Errors.Should().NotContain(
            e => e.Contains("predicate"),
            "'method' takes precedence, so an ignored 'predicate' must not fail submission");
    }

    [UnitTest]
    public void Build_EnrichmentInvalidPredicateWithoutMethod_IsStillRejected()
    {
        // The precedence relaxation must not blanket-disable predicate validation: with no
        // 'method', the executor DOES read 'predicate', so a bad value must still fail.
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["layerId"] = "7",
            ["datasetId"] = "demographics",
            ["predicate"] = "not-a-predicate",
        };

        var plan = GpPlanner.Build("enrichment.enrich", inputs, Catalog(), DefaultCap, callerInputBytes: 0);

        plan.Should().NotBeNull();
        plan!.IsValid.Should().BeFalse();
        plan.Errors.Should().Contain(e => e.Contains("predicate"));
    }

    [UnitTest]
    public void GraphValidator_PlanWithCycle_IsRejected()
    {
        // The CLI plan path delegates structural validation to the SAME shared
        // AnalysisPlanGraphValidator the durable submit path uses; a cycle must be
        // caught here, mirroring GeoprocessingJobService.ValidatePlanStructure.
        var plan = new AnalysisPlan
        {
            PlanId = "p",
            IntentId = "i",
            Steps =
            [
                Step("a", dependsOn: ["b"]),
                Step("b", dependsOn: ["a"]),
            ],
        };

        var act = () => AnalysisPlanGraphValidator.Validate(plan);

        act.Should().Throw<Exception>().Where(e => e.Message.Contains("cycle"));
    }

    [UnitTest]
    public void GraphValidator_PlanWithDanglingDependency_IsRejected()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p",
            IntentId = "i",
            Steps =
            [
                Step("a", dependsOn: ["ghost"]),
            ],
        };

        var act = () => AnalysisPlanGraphValidator.Validate(plan);

        act.Should().Throw<Exception>().Where(e => e.Message.Contains("unknown step") && e.Message.Contains("ghost"));
    }

    [UnitTest]
    public void GraphValidator_AcyclicPlan_OrdersDependenciesBeforeDependents()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p",
            IntentId = "i",
            Steps =
            [
                Step("c", dependsOn: ["a", "b"]),
                Step("b", dependsOn: ["a"]),
                Step("a"),
            ],
        };

        AnalysisPlanGraphValidator.Validate(plan);
        var ordered = AnalysisPlanGraphValidator.TopologicalOrder(plan).Select(s => s.StepId).ToArray();

        ordered.Should().Equal("a", "b", "c");
    }

    [UnitTest]
    public void Build_LargeInputBuffer_EstimatesOverCapAndWarns()
    {
        // 12 MiB of input × the buffer ~5x growth heuristic crosses the 50 MiB cap.
        const long twelveMiB = 12L * 1024L * 1024L;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
        };

        var plan = GpPlanner.Build("geometry.buffer", inputs, Catalog(), DefaultCap, callerInputBytes: twelveMiB);

        plan.Should().NotBeNull();
        // Params are valid; the only signal is a size warning, so the plan stays valid.
        plan!.IsValid.Should().BeTrue();
        plan.Estimate.InputBytes.Should().Be(twelveMiB);
        plan.Estimate.EstimatedOutputBytes.Should().BeGreaterThan(DefaultCap);
        plan.Estimate.ExceedsCap.Should().BeTrue();
        plan.Warnings.Should().Contain(w => w.Contains("MaxArtifactBytes") && w.Contains("exceed"));
        plan.Warnings.Should().Contain(w => w.Contains("heuristic"));
    }

    [UnitTest]
    public void Build_SmallInputBuffer_StaysUnderCapWithNoSizeWarning()
    {
        const long oneMiB = 1L * 1024L * 1024L;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
        };

        var plan = GpPlanner.Build("geometry.buffer", inputs, Catalog(), DefaultCap, callerInputBytes: oneMiB);

        plan!.Estimate.ExceedsCap.Should().BeFalse();
        plan.Warnings.Should().NotContain(w => w.Contains("exceed"));
    }

    [UnitTest]
    public void Estimate_ScalarOutputProcess_IsNeverACapRisk()
    {
        // geometry.area emits a scalar; even a huge input cannot blow the cap.
        var definition = Catalog().GetProcess("geometry.area");
        definition.Should().NotBeNull();
        definition!.OutputArtifactKinds.Should().Contain(ArtifactKind.Scalar);

        var estimate = GpSizeEstimator.Estimate(definition, callerInputBytes: 500L * 1024L * 1024L, DefaultCap);

        estimate.ExceedsCap.Should().BeFalse();
        estimate.EstimatedOutputBytes.Should().BeLessThan(DefaultCap);
    }

    [UnitTest]
    public void Estimate_RasterClassProcess_FlagsLongRunning()
    {
        var definition = Catalog().GetProcess("raster.reproject");
        definition.Should().NotBeNull();
        definition!.RuntimeProfile.Should().Be(RuntimeProfiles.Native);

        var estimate = GpSizeEstimator.Estimate(definition, callerInputBytes: 1024, DefaultCap);

        estimate.LongRunning.Should().BeTrue();
    }

    [UnitTest]
    public void Estimate_NoFileInput_DoesNotFabricateAnOutputSize()
    {
        var definition = Catalog().GetProcess("geometry.buffer")!;

        var estimate = GpSizeEstimator.Estimate(definition, callerInputBytes: 0, DefaultCap);

        estimate.EstimatedOutputBytes.Should().BeNull();
        estimate.ExceedsCap.Should().BeFalse();
    }

    private static AnalysisPlanStep Step(string id, string[]? dependsOn = null) => new()
    {
        StepId = id,
        Kind = AnalysisPlanStepKind.Geoprocess,
        ProcessId = "geometry.buffer",
        Inputs = new Dictionary<string, string>(StringComparer.Ordinal),
        DependsOn = dependsOn ?? [],
    };
}
