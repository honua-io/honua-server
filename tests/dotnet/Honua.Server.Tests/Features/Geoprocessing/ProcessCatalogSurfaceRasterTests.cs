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
        // 6 gdaldem-backed surface products + surface.contour / surface.viewshed
        // (gdal_contour / gdal_viewshed; #2240).
        _catalog.GetProcessesByCategory("surface").Should().HaveCount(8);
        // 5 native raster idioms (clip, reproject, statistics, histogram,
        // zonal-statistics) + the raster analysis tool pack (resample,
        // interpolate-idw, interpolate-kriging, mosaic; #2141) + gdal.gdalwarp
        // (the native-profile raster reproject executed out-of-process by the
        // GDAL worker) + the map-algebra tool pack (map-algebra, spectral-index,
        // reclassify; #2239).
        _catalog.GetProcessesByCategory("raster").Should().HaveCount(13);
        // 4 managed conversion idioms + gdal.ogr2ogr (the native-profile vector
        // conversion executed out-of-process by the GDAL worker) + pcloud.translate
        // (the native-profile LAZ/COPC decompress + projected-CRS reproject, #1854)
        // + conversion.polygonize / conversion.rasterize (#2240).
        _catalog.GetProcessesByCategory("conversion").Should().HaveCount(8);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_PointCloudTranslate_DeclaresNativeProfile_RequiresSource_OptionalSourceSrs()
    {
        // pcloud.translate is executed out-of-process by the GDAL/PDAL worker
        // (PdalPointCloudConvertJobExecutor), so it MUST declare the native
        // runtime profile and REQUIRE the canonical 'source' base64 LAZ/COPC input
        // while keeping the reprojection 'sourceSrs' hint optional (a geographic
        // source is decompressed verbatim).
        var definition = _catalog.GetProcess("pcloud.translate");

        definition.Should().NotBeNull();
        definition!.RuntimeProfile.Should().Be(
            Core.Features.ControlPlane.Domain.RuntimeProfiles.Native);
        definition.Category.Should().Be("conversion");
        definition.Parameters.Should().Contain(p => p.Name == "source" && p.Required);
        definition.Parameters.Should().Contain(p => p.Name == "sourceSrs" && !p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_PointCloudTranslate_WithoutSource_ProducesMissingRequiredParameterViolation()
    {
        // The native worker decompresses inline LAZ/COPC bytes; a plan without
        // 'source' would route to the worker and fail there, so the catalog
        // rejects it at submit-time validation instead.
        var plan = CreateSingleStepPlan(
            "pcloud.translate",
            new Dictionary<string, string>
            {
                ["sourceSrs"] = "EPSG:32610"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.source");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_PointCloudTranslate_WithProjectedSourceCrs_ProducesNoViolations()
    {
        // A projected source CRS (UTM zone 10N) is the reprojection path: it must
        // pass validation so the worker reprojects to geographic EPSG:4979.
        var plan = CreateSingleStepPlan(
            "pcloud.translate",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["sourceSrs"] = "EPSG:32610"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_PointCloudTranslate_WithBareEpsgInteger_ProducesNoViolations()
    {
        var plan = CreateSingleStepPlan(
            "pcloud.translate",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["sourceSrs"] = "26910"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_PointCloudTranslate_WithInjectionSourceCrs_ProducesViolation()
    {
        // The submit-time token guard mirrors the worker's IsValidSrsToken so a
        // shell-influencing CRS value is rejected before it can reach the CLI.
        var plan = CreateSingleStepPlan(
            "pcloud.translate",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["sourceSrs"] = "EPSG:32610; rm -rf /"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.sourceSrs");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_PointCloudTranslate_WithoutSourceSrs_ProducesNoViolations()
    {
        // A geographic source omits sourceSrs entirely; the worker decompresses
        // verbatim. Omission must not be flagged.
        var plan = CreateSingleStepPlan(
            "pcloud.translate",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_SurfaceAndRasterProcesses_DeclareNativeProfile_AndOfferRasterSourceSelectors()
    {
        // The native worker pipeline reads a base64 GeoTIFF from the canonical
        // 'source' input. As of #2264 the submit path can also materialize
        // 'source' from a registered catalog raster referenced by 'layerId' or
        // 'rasterId', so all three selectors are declared OPTIONAL and the
        // "supply exactly one" rule is enforced by the validator
        // (ValidateSharedRasterSourceSemantics) rather than the per-parameter
        // Required flag. A plan that omits all three is rejected at submit-time
        // (see Validator_SurfaceSlope_WithoutSource_... below) rather than
        // routing to the worker with no readable input and failing there.
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
            definition.Parameters.Should().Contain(p => p.Name == "source" && !p.Required,
                $"'{processId}' must advertise the canonical 'source' base64 GeoTIFF selector as OPTIONAL now that layerId/rasterId resolution lands the bytes (#2264)");
            definition.Parameters.Should().Contain(p => p.Name == "layerId" && !p.Required,
                $"'{processId}' must advertise the 'layerId' raster selector as OPTIONAL");
            definition.Parameters.Should().Contain(p => p.Name == "rasterId" && !p.Required,
                $"'{processId}' must advertise the 'rasterId' raster selector as OPTIONAL");
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

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_RasterAnalyticToolPack_IsRegistered_WithNativeProfileAndRasterOutput()
    {
        // The #2141 raster analysis tool pack is executed out-of-process by the
        // native GDAL worker, so each entry MUST declare the native runtime profile.
        string[] toolPackIds =
        [
            "raster.resample", "raster.interpolate-idw",
            "raster.interpolate-kriging", "raster.mosaic",
        ];
        foreach (var processId in toolPackIds)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull($"catalog must advertise raster tool '{processId}'");
            definition!.Category.Should().Be("raster");
            definition.RuntimeProfile.Should().Be(
                Core.Features.ControlPlane.Domain.RuntimeProfiles.Native,
                $"'{processId}' is executed by the native worker");
            definition.OutputArtifactKinds.Should().ContainSingle().Which.Should().Be(ArtifactKind.Raster);
        }

        // 'source' is an OPTIONAL raster selector (#2264 — supply one of
        // source/layerId/rasterId, enforced by the validator); 'cellSize' is the
        // genuinely-required resample parameter.
        _catalog.GetProcess("raster.resample")!.Parameters
            .Should().Contain(p => p.Name == "source" && !p.Required)
            .And.Contain(p => p.Name == "cellSize" && p.Required);
        _catalog.GetProcess("raster.interpolate-idw")!.Parameters
            .Should().Contain(p => p.Name == "points" && p.Required);
        _catalog.GetProcess("raster.mosaic")!.Parameters
            .Should().Contain(p => p.Name == "sources" && p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterResample_WithoutCellSize_ProducesMissingRequiredParameterViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterResample_WithNonPositiveCellSize_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["cellSize"] = "0",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterResample_WithUnknownResampling_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["cellSize"] = "30",
                ["resampling"] = "spline",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.resampling");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterInterpolateIdw_WithValidTuning_ProducesNoViolations()
    {
        var plan = CreateSingleStepPlan(
            "raster.interpolate-idw",
            new Dictionary<string, string>
            {
                ["points"] = StubBase64,
                ["zField"] = "elevation",
                ["power"] = "2.5",
                ["smoothing"] = "0",
                ["width"] = "256",
                ["height"] = "256",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterInterpolateIdw_WithoutPoints_ProducesMissingRequiredParameterViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.interpolate-idw",
            new Dictionary<string, string>
            {
                ["power"] = "2",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.points");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterInterpolateIdw_WithNonPositivePower_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.interpolate-idw",
            new Dictionary<string, string>
            {
                ["points"] = StubBase64,
                ["power"] = "-1",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.power");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterInterpolateIdw_WithWidthWithoutHeight_ProducesViolation()
    {
        // gdal_grid -outsize needs both dimensions; the executor rejects a
        // half-specified grid, so submit-time validation must too.
        var plan = CreateSingleStepPlan(
            "raster.interpolate-idw",
            new Dictionary<string, string>
            {
                ["points"] = StubBase64,
                ["width"] = "256",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.FieldPath == "steps[s1].inputs.width");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterInterpolateKriging_WithPoints_PassesValidation_AsFlaggedButShapeValid()
    {
        // Kriging is flagged unsupported at execution, but a shape-valid plan must
        // still pass submit-time validation so the worker can surface the
        // unsupported-dependency message as a job failure (not a submit rejection).
        var plan = CreateSingleStepPlan(
            "raster.interpolate-kriging",
            new Dictionary<string, string>
            {
                ["points"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterMosaic_WithUnsupportedOperator_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.mosaic",
            new Dictionary<string, string>
            {
                ["sources"] = StubBase64,
                ["operator"] = "mean",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.operator");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterMosaic_WithoutSources_ProducesMissingRequiredParameterViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.mosaic",
            new Dictionary<string, string>
            {
                ["operator"] = "last",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.sources");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_RasterAnalysisAndTerrainToolPack_DeclaresNativeProfile()
    {
        // The #2239/#2240 tool pack is executed out-of-process by the native GDAL
        // worker, so each entry MUST declare the native runtime profile.
        string[] toolPackIds =
        [
            "raster.map-algebra", "raster.spectral-index", "raster.reclassify",
            "proximity.euclidean-distance", "proximity.euclidean-allocation",
            "surface.contour", "surface.viewshed",
            "conversion.polygonize", "conversion.rasterize",
        ];
        foreach (var processId in toolPackIds)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull($"catalog must advertise GP tool '{processId}'");
            definition!.RuntimeProfile.Should().Be(
                Core.Features.ControlPlane.Domain.RuntimeProfiles.Native,
                $"'{processId}' is executed by the native worker");
        }

        _catalog.GetProcess("raster.map-algebra")!.Parameters
            .Should().Contain(p => p.Name == "sources" && p.Required)
            .And.Contain(p => p.Name == "expression" && p.Required);
        _catalog.GetProcess("raster.reclassify")!.Parameters
            .Should().Contain(p => p.Name == "remap" && p.Required);
        _catalog.GetProcess("conversion.polygonize")!.OutputArtifactKinds
            .Should().ContainSingle().Which.Should().Be(ArtifactKind.FeatureLayer);
        _catalog.GetProcess("surface.contour")!.OutputArtifactKinds
            .Should().ContainSingle().Which.Should().Be(ArtifactKind.FeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterMapAlgebra_WithDisallowedExpression_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.map-algebra",
            new Dictionary<string, string>
            {
                ["sources"] = StubBase64,
                ["expression"] = "__import__('os')",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.expression");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterReclassify_WithNonNumericRemap_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.reclassify",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["remap"] = "lo..hi:1",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.remap");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_RasterSpectralIndex_MissingRequiredBand_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "raster.spectral-index",
            new Dictionary<string, string>
            {
                ["index"] = "NDVI",
                ["nir"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.FieldPath == "steps[s1].inputs.red");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ProximityEuclideanAllocation_ShapeValid_PassesValidation()
    {
        // Allocation (#2255) shares the distance op's parameter surface; a plan
        // supplying only the required 'source' must pass submit-time validation.
        var plan = CreateSingleStepPlan(
            "proximity.euclidean-allocation",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ProximityEuclideanAllocation_WithInvalidDistUnits_ProducesViolation()
    {
        // Allocation reuses the distance semantic validator (#2255), so an
        // out-of-enum distUnits is rejected at submit time.
        var plan = CreateSingleStepPlan(
            "proximity.euclidean-allocation",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["distUnits"] = "MILES",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.FieldPath == "steps[s1].inputs.distUnits");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_SurfaceContour_WithoutInterval_ProducesMissingRequiredParameterViolation()
    {
        var plan = CreateSingleStepPlan(
            "surface.contour",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER"
            && v.FieldPath == "steps[s1].inputs.interval");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ConversionRasterize_WithBothBurnAndAttribute_ProducesViolation()
    {
        var plan = CreateSingleStepPlan(
            "conversion.rasterize",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["burnValue"] = "1",
                ["attribute"] = "pop",
                ["cellSize"] = "0.001",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.FieldPath == "steps[s1].inputs.burnValue");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ConversionRasterize_WithGridAndBurn_ProducesNoViolations()
    {
        var plan = CreateSingleStepPlan(
            "conversion.rasterize",
            new Dictionary<string, string>
            {
                ["source"] = StubBase64,
                ["burnValue"] = "1",
                ["cellSize"] = "0.001",
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
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
