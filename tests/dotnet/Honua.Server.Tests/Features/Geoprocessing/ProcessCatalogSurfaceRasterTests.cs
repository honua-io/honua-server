// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Protocol(Protocols.Grpc)]
public sealed class ProcessCatalogSurfaceRasterTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_SurfaceRasterAndConversionCategories_AreRegistered()
    {
        _catalog.GetProcessesByCategory("surface").Should().HaveCount(6);
        _catalog.GetProcessesByCategory("raster").Should().HaveCount(5);
        _catalog.GetProcessesByCategory("conversion").Should().HaveCount(4);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_RasterZonalStatistics_DeclaresTableOutput()
    {
        var definition = _catalog.GetProcess("raster.zonal-statistics");

        definition.Should().NotBeNull();
        definition!.OutputArtifactKinds.Should().ContainSingle()
            .Which.Should().Be(ArtifactKind.Table);
        definition.Parameters.Should().Contain(p => p.Name == "zonesLayerId" && p.Required);
        definition.Parameters.Should().Contain(p => p.Name == "band");
        definition.Parameters.Should().Contain(p => p.Name == "statistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceHillshade_InvalidAltitude_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "surface.hillshade",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["rasterId"] = "922337203685477",
                ["altitude"] = "91"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.altitude");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterSource_With64BitRasterId_ProducesNoViolations()
    {
        var plan = CreateSingleStepPlan(
            "raster.statistics",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["rasterId"] = "922337203685477580"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceRugosity_WithWindowRadiusOtherThanOne_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "surface.rugosity-tri",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["windowRadius"] = "2"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.windowRadius");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterZonalStatistics_WithUnknownStatistic_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.zonal-statistics",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["zonesLayerId"] = "8",
                ["statistics"] = "count,p95"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.statistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ConversionRasterFormat_WithUnknownTargetFormat_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "conversion.raster-format",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["targetFormat"] = "bmp"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.targetFormat");
    }

    private static AnalysisPlan CreateSingleStepPlan(
        string processId,
        IReadOnlyDictionary<string, string> inputs) =>
        new()
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = new Dictionary<string, string>(inputs)
                }
            ]
        };
}
