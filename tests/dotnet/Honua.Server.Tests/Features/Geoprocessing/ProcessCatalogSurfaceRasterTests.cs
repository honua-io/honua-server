// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Protocol(TestProtocols.Grpc)]
public sealed class ProcessCatalogSurfaceRasterTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_SurfaceRasterAndConversionCategories_AreRegistered()
    {
        _catalog.GetProcessesByCategory("surface").Should().HaveCount(6);
        // 5 native raster idioms + gdal.gdalwarp (the native-profile raster
        // reproject executed out-of-process by the GDAL worker).
        _catalog.GetProcessesByCategory("raster").Should().HaveCount(6);
        // 4 managed conversion idioms + gdal.ogr2ogr (the native-profile vector
        // conversion executed out-of-process by the GDAL worker).
        _catalog.GetProcessesByCategory("conversion").Should().HaveCount(5);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_SurfaceAndRasterProcesses_DeclareNativeProfile_AndRequireSourceInput()
    {
        // The native worker pipeline reads a base64 GeoTIFF from the canonical
        // 'source' input — and ONLY that input today (layer-resolved sourcing is
        // a follow-on). The catalog declares 'source' REQUIRED so plans that
        // omit it fail at submit-time validation rather than routing to the
        // worker and failing there. layerId/rasterId stay optional placeholders
        // for the deferred resolution path.
        string[] nativeProcessIds =
        [
            "surface.slope", "surface.aspect", "surface.hillshade",
            "surface.rugosity-tri", "surface.rugosity-tpi", "surface.roughness",
            "raster.clip", "raster.reproject", "raster.statistics",
            "raster.histogram", "raster.zonal-statistics",
        ];
        foreach (var processId in nativeProcessIds)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull($"catalog must advertise native process '{processId}'");
            definition!.RuntimeProfile.Should().Be(
                Core.Features.ControlPlane.Domain.RuntimeProfiles.Native,
                $"'{processId}' is executed by the native worker");
            definition.Parameters.Should().Contain(p => p.Name == "source" && p.Required,
                $"'{processId}' must REQUIRE the canonical 'source' base64 GeoTIFF input until layer-resolved sourcing lands");
            definition.Parameters.Should().Contain(p => p.Name == "layerId" && !p.Required,
                $"'{processId}' must keep 'layerId' OPTIONAL until layer-resolved sourcing lands");
        }

        // raster.zonal-statistics additionally REQUIRES an inline 'zones' input;
        // zonesLayerId stays optional as the deferred resolution placeholder.
        var zonal = _catalog.GetProcess("raster.zonal-statistics");
        zonal!.Parameters.Should().Contain(p => p.Name == "zones" && p.Required,
            "raster.zonal-statistics must REQUIRE the canonical inline 'zones' GeoJSON input");
        zonal.Parameters.Should().Contain(p => p.Name == "zonesLayerId" && !p.Required,
            "raster.zonal-statistics must keep 'zonesLayerId' OPTIONAL until zones-layer resolution lands");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceSlope_Radians_ProducesViolation()
    {
        // gdaldem does not emit radians directly. The validator must reject
        // radians/radian up front so plans accepted here are also accepted at
        // execution time by the GdalSurfaceJobExecutor.
        var plan = CreateSingleStepPlan(
            "surface.slope",
            new Dictionary<string, string>
            {
                ["layerId"] = "7",
                ["units"] = "radians"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.units");
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
        // Native worker reads inline 'zones' (base64 GeoJSON FeatureCollection);
        // zonesLayerId is reserved for the deferred layer-resolution path so it
        // is OPTIONAL today. Plans must supply 'zones' instead.
        definition.Parameters.Should().Contain(p => p.Name == "zones" && p.Required);
        definition.Parameters.Should().Contain(p => p.Name == "zonesLayerId" && !p.Required);
        definition.Parameters.Should().Contain(p => p.Name == "band");
        definition.Parameters.Should().Contain(p => p.Name == "statistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceHillshade_InvalidAltitude_ProducesViolation()
    {
        // 'source' is required by the native catalog entries; supply a token
        // value so only the field under test produces a violation.
        var plan = CreateSingleStepPlan(
            "surface.hillshade",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
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
                ["source"] = StubBase64,
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
                ["source"] = StubBase64,
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
                ["source"] = StubBase64,
                ["zones"] = StubBase64,
                ["statistics"] = "count,p95"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.statistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceSlope_WithoutSource_ProducesMissingRequiredParameterViolation()
    {
        // Native catalog entries declare 'source' REQUIRED so plans that route
        // to the GDAL worker without inline raster bytes fail at validation
        // rather than reaching the worker and failing there.
        var plan = CreateSingleStepPlan(
            "surface.slope",
            new Dictionary<string, string>
            {
                ["units"] = "degrees"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.source");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterZonalStatistics_WithoutZones_ProducesMissingRequiredParameterViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.zonal-statistics",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.zones");
    }

    // Any non-empty base64 string passes the Text type-validation; the validator
    // does not decode source/zones, it only enforces presence.
    private const string StubBase64 = "dGVzdA==";

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
