// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the built-in process catalog and plan validator.
/// </summary>
[Protocol(TestProtocols.Grpc)]
public sealed class ProcessCatalogTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    // -----------------------------------------------------------------------
    // Catalog — non-empty and discoverable
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_ListProcesses_ReturnsExactly57BuiltIns()
    {
        var all = _catalog.ListProcesses();

        // 38 original trunk processes + 13 GeoETL transform/source/sink processes
        // reconciled from feat/geoetl-baseline + 1 managed spatial-join
        // (analytics.spatial-join-managed) added for the workflow/codemod job path
        // + 3 managed analytics counterparts (analytics.cluster-managed,
        // analytics.buffer-aggregate-managed, analytics.density-managed) added by
        // #1260 + 2 native-profile GDAL worker processes (gdal.gdalwarp,
        // gdal.ogr2ogr) reconciled from feat/gdal-heavy-worker.
        all.Should().HaveCount(57);
        all.Select(p => p.ProcessId).Should().OnlyHaveUniqueItems();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GeometryCategory_Returns14Processes()
    {
        var geometry = _catalog.GetProcessesByCategory("geometry");

        geometry.Should().HaveCount(14);
        geometry.Should().AllSatisfy(p => p.Category.Should().Be("geometry"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AnalyticsCategory_Returns8Processes()
    {
        var analytics = _catalog.GetProcessesByCategory("analytics");

        // 4 trunk analytics + analytics.spatial-join-managed +
        // 3 managed counterparts from #1260 (analytics.cluster-managed,
        // analytics.buffer-aggregate-managed, analytics.density-managed) — the
        // job-dispatchable managed counterparts to the PostGIS-protocol entries.
        analytics.Should().HaveCount(8);
        analytics.Should().AllSatisfy(p => p.Category.Should().Be("analytics"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GeneralizationCategory_Returns2Processes()
    {
        var generalization = _catalog.GetProcessesByCategory("generalization");

        generalization.Should().HaveCount(2);
        generalization.Should().AllSatisfy(p => p.Category.Should().Be("generalization"));
        generalization.Select(p => p.ProcessId).Should().BeEquivalentTo(
            "generalization.simplify-layer",
            "generalization.dissolve");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_DataManagementCategory_Returns3Processes()
    {
        var dataManagement = _catalog.GetProcessesByCategory("data-management");

        dataManagement.Should().HaveCount(3);
        dataManagement.Should().AllSatisfy(p => p.Category.Should().Be("data-management"));
        dataManagement.Select(p => p.ProcessId).Should().BeEquivalentTo(
            "data-management.copy-features",
            "data-management.delete-features",
            "data-management.calculate-field");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GeneralizationSimplifyLayer_DeclaresFeatureLayerOutput()
    {
        var definition = _catalog.GetProcess("generalization.simplify-layer");

        definition.Should().NotBeNull();
        definition!.OutputArtifactKinds.Should().ContainSingle()
            .Which.Should().Be(ArtifactKind.FeatureLayer);
        definition.Parameters.Should().Contain(p => p.Name == "layerId" && p.Required)
            .And.Contain(p => p.Name == "tolerance" && p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_DataManagementDeleteFeatures_DeclaresScalarOutput()
    {
        var definition = _catalog.GetProcess("data-management.delete-features");

        definition.Should().NotBeNull();
        definition!.OutputArtifactKinds.Should().ContainSingle()
            .Which.Should().Be(ArtifactKind.Scalar);
        definition.Parameters.Should().Contain(p => p.Name == "layerId" && p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_DataManagementCalculateField_RequiresFieldNameAndExpression()
    {
        var definition = _catalog.GetProcess("data-management.calculate-field");

        definition.Should().NotBeNull();
        definition!.Parameters.Should().Contain(p => p.Name == "fieldName" && p.Required);
        definition.Parameters.Should().Contain(p => p.Name == "expression" && p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcess_ReturnsDefinitionForKnownId()
    {
        var buffer = _catalog.GetProcess("geometry.buffer");

        buffer.Should().NotBeNull();
        buffer!.ProcessId.Should().Be("geometry.buffer");
        buffer.Title.Should().Be("Buffer");
        buffer.Category.Should().Be("geometry");
        buffer.Parameters.Should().NotBeEmpty();
        buffer.OutputArtifactKinds.Should().Contain(ArtifactKind.FeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcess_ReturnsNullForUnknownId()
    {
        var result = _catalog.GetProcess("nonexistent.process");

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcessesByCategory_ReturnsEmptyForUnknownCategory()
    {
        var result = _catalog.GetProcessesByCategory("nonexistent");

        result.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AllProcesses_HaveRequiredFields()
    {
        var all = _catalog.ListProcesses();

        all.Should().AllSatisfy(p =>
        {
            p.ProcessId.Should().NotBeNullOrWhiteSpace();
            p.Title.Should().NotBeNullOrWhiteSpace();
            p.Description.Should().NotBeNullOrWhiteSpace();
            p.Category.Should().NotBeNullOrWhiteSpace();
            p.Parameters.Should().NotBeNull();
            p.OutputArtifactKinds.Should().NotBeEmpty();
        });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AllParameters_HaveRequiredFields()
    {
        var all = _catalog.ListProcesses();

        all.SelectMany(p => p.Parameters).Should().AllSatisfy(param =>
        {
            param.Name.Should().NotBeNullOrWhiteSpace();
            param.DisplayName.Should().NotBeNullOrWhiteSpace();
            param.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_EachExpectedProcess_IsDiscoverable()
    {
        string[] expectedIds =
        [
            "geometry.buffer", "geometry.simplify", "geometry.project",
            "geometry.make-valid", "geometry.union", "geometry.intersect",
            "geometry.clip", "geometry.difference", "geometry.area",
            "geometry.length", "geometry.centroid", "geometry.convex-hull",
            "geometry.dissolve", "geometry.snap",
            "analytics.cluster", "analytics.spatial-join",
            "analytics.spatial-join-managed",
            "analytics.cluster-managed",
            "analytics.buffer-aggregate-managed",
            "analytics.density-managed",
            "analytics.buffer-aggregate", "analytics.density",
            "surface.slope", "surface.aspect", "surface.hillshade",
            "surface.rugosity-tri", "surface.rugosity-tpi", "surface.roughness",
            "raster.clip", "raster.reproject", "raster.statistics",
            "raster.histogram", "raster.zonal-statistics",
            "conversion.geometry-format", "conversion.feature-project",
            "conversion.raster-format", "conversion.raster-reproject",
            "generalization.simplify-layer", "generalization.dissolve",
            "data-management.copy-features", "data-management.delete-features",
            "data-management.calculate-field",
            // GeoETL transform/source/sink processes reconciled from feat/geoetl-baseline.
            "transform.attribute-rename", "transform.attribute-cast",
            "transform.computed-field", "transform.attribute-filter",
            "transform.spatial-filter", "transform.clip",
            "transform.dedup", "transform.reproject",
            "source.geojson", "source.csv",
            "sink.geojson-file", "sink.quarantine", "sink.external-postgis"
        ];

        foreach (var processId in expectedIds)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                $"process '{processId}' must be registered in the built-in catalog");
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_MeasurementProcesses_ProduceScalarArtifacts()
    {
        _catalog.GetProcess("geometry.area")!.OutputArtifactKinds
            .Should().Contain(ArtifactKind.Scalar);

        _catalog.GetProcess("geometry.length")!.OutputArtifactKinds
            .Should().Contain(ArtifactKind.Scalar);
    }

    // -----------------------------------------------------------------------
    // Validator — process resolution
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_KnownProcess_WithRequiredParams_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ExternalPostgisSink_WithSecureConnectionName_ProducesNoViolations()
    {
        var plan = CreateSingleStepPlan(
            "sink.external-postgis",
            new Dictionary<string, string>
            {
                ["input"] = "data:application/geo+json;base64,e30=",
                ["connectionName"] = "external-target",
                ["table"] = "external_out",
                ["targetSrid"] = "4326"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ExternalPostgisSink_WithInlineConnectionString_RejectsPlan()
    {
        var plan = CreateSingleStepPlan(
            "sink.external-postgis",
            new Dictionary<string, string>
            {
                ["input"] = "data:application/geo+json;base64,e30=",
                ["connectionString"] = "Host=localhost;Database=external",
                ["table"] = "external_out",
                ["targetSrid"] = "4326"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.FieldPath == "steps[s1].inputs.connectionString" &&
            v.Message.Contains("inline connection strings are not accepted", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_ExternalPostgisSink_WithoutSecureConnection_RejectsPlan()
    {
        var plan = CreateSingleStepPlan(
            "sink.external-postgis",
            new Dictionary<string, string>
            {
                ["input"] = "data:application/geo+json;base64,e30=",
                ["table"] = "external_out",
                ["targetSrid"] = "4326"
            });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.FieldPath == "steps[s1].inputs.connectionName" &&
            v.Code == "MISSING_REQUIRED_PARAMETER");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_UnknownProcess_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "unknown.process"
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "UNKNOWN_PROCESS");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_MissingProcessId_OnGeoprocessStep_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = null
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "MISSING_PROCESS_ID");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_MissingRequiredParameter_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.Code == "MISSING_REQUIRED_PARAMETER");
        violations.Where(v => v.Code == "MISSING_REQUIRED_PARAMETER")
            .Should().HaveCount(2, "srid and distance are both required");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_NonGeoprocessSteps_AreSkipped()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.QueryFeatures
                },
                new AnalysisPlanStep
                {
                    StepId = "s2",
                    Kind = AnalysisPlanStepKind.RenderMap
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_UnknownInputKey_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100",
                        ["distnace"] = "200"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "UNKNOWN_PARAMETER");
        violations.Single(v => v.Code == "UNKNOWN_PARAMETER")
            .FieldPath.Should().Be("steps[s1].inputs.distnace");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_OptionalParameters_DoNotProduceViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty("geodesic is optional and should not cause violations");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_OmittingAlgorithm_DefaultsToDbscanAndRequiresEpsMinPoints()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        // Omitted algorithm defaults to DBSCAN in the handler, which then
        // requires eps and minPoints — so catalog validation must match.
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.eps");
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.minPoints");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_Dbscan_WithEpsAndMinPoints_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_KMeansWithoutK_ProducesConditionalRequiredViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "kmeans"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.k");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_InvalidAlgorithmValue_ProducesEnumViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "hierarchical"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.algorithm");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_NonPositiveEps_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "0",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.eps");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_KZero_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "kmeans",
                        ["k"] = "0"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.k");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_OmittingPredicate_DoesNotProduceViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty(
            "the spatial-join handler defaults predicate to intersects when omitted");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_UsesCanonicalDistanceParameter()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["predicate"] = "dwithin",
                        ["distance"] = "250"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty(
            "catalog must use the shared 'distance' contract name, not 'distanceMeters'");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_DwithinWithoutDistance_ProducesConditionalRequiredViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["predicate"] = "dwithin"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_InvalidPredicate_ProducesEnumViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["predicate"] = "touches"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.predicate");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_NonPositiveDistance_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["predicate"] = "dwithin",
                        ["distance"] = "-1"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_OmittingMode_DoesNotProduceViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "500"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty(
            "the density handler defaults mode to hex when omitted and uses 'cellSize' (not 'cellSizeMeters')");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_RejectsDeprecatedCellSizeMeters()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSizeMeters"] = "500"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.Code == "UNKNOWN_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.cellSizeMeters");
        violations.Should().Contain(v => v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_InvalidMode_ProducesEnumViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["mode"] = "triangle",
                        ["cellSize"] = "500"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.mode");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_NonPositiveCellSize_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "0"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_InvalidUnit_ProducesEnumViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "100",
                        ["unit"] = "leagues"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.unit");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_UnitAlias_IsAccepted()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "5",
                        ["unit"] = "km"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty("handler accepts 'km' as an alias for kilometers");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_DefaultsMatchHandler()
    {
        var bufferAggregate = _catalog.GetProcess("analytics.buffer-aggregate");

        bufferAggregate.Should().NotBeNull();
        var dissolve = bufferAggregate!.Parameters.Single(p => p.Name == "dissolve");
        dissolve.DefaultValue.Should().Be(
            "true",
            "buffer-aggregate handler defaults dissolve to true when omitted; catalog must match");
    }

    // -----------------------------------------------------------------------
    // Validator — typed value validation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryBuffer_InvalidTypedInputs_ProducesViolationPerField()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "not-base64!!",
                        ["srid"] = "abc",
                        ["distance"] = "not-a-number",
                        ["geodesic"] = "maybe"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        var invalid = violations.Where(v => v.Code == "INVALID_PARAMETER_VALUE").ToList();
        invalid.Should().HaveCount(4, "each typed input should report its own INVALID_PARAMETER_VALUE");
        invalid.Select(v => v.FieldPath).Should().BeEquivalentTo(
        [
            "steps[s1].inputs.wkb",
            "steps[s1].inputs.srid",
            "steps[s1].inputs.distance",
            "steps[s1].inputs.geodesic"
        ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_InvalidTypedInputs_ProducesViolationPerField()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "   ",
                        ["algorithm"] = "DbScan",
                        ["eps"] = "NaN",
                        ["minPoints"] = "5.5",
                        ["returnHullPerCluster"] = "yes"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        var invalid = violations.Where(v => v.Code == "INVALID_PARAMETER_VALUE").ToList();
        invalid.Select(v => v.FieldPath).Should().BeEquivalentTo(
        [
            "steps[s1].inputs.layerId",
            "steps[s1].inputs.eps",
            "steps[s1].inputs.minPoints",
            "steps[s1].inputs.returnHullPerCluster"
        ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryBuffer_SridZero_IsRejected()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "0",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.srid");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryUnion_WkbArray_AcceptsJsonArrayOfBase64()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.union",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkbs"] = "[\"AAAA\",\"BBBB\"]",
                        ["srid"] = "4326"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryUnion_MalformedWkbArray_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.union",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkbs"] = "AAAA,BBBB",
                        ["srid"] = "4326"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.wkbs");
    }

    // -----------------------------------------------------------------------
    // Validator — handler parity: configured analytics bounds, blank-conditional
    //   ordering, and buffer non-negative distance with unit-aware cap.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_BlankEpsWithDbscan_ReportsMissingNotInvalid()
    {
        // Handler treats IsNullOrWhiteSpace values as "not supplied". The
        // validator must match: blank eps under algorithm=dbscan should surface
        // MISSING_REQUIRED_PARAMETER rather than a stray INVALID_PARAMETER_VALUE.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.FieldPath == "steps[s1].inputs.eps");
        violations.Single(v => v.FieldPath == "steps[s1].inputs.eps").Code
            .Should().Be("MISSING_REQUIRED_PARAMETER");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_EpsExceedsMaxDbscanEps_ProducesRangeViolation()
    {
        // Handler caps eps at AnalyticsLimits.MaxDbscanEpsMeters (default 100_000).
        // The validator must reject the same out-of-range value instead of waiting
        // for the handler to reject it at execution time.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "500000",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.eps");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_KExceedsMaxKMeansK_ProducesRangeViolation()
    {
        // Handler caps k at AnalyticsLimits.MaxKMeansK (default 1_000). The
        // validator must reject values above the configured upper bound.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "kmeans",
                        ["k"] = "5000"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.k");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_DistanceExceedsMaxDWithin_ProducesRangeViolation()
    {
        // Handler caps dwithin distance at AnalyticsLimits.MaxDWithinDistanceMeters.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["predicate"] = "dwithin",
                        ["distance"] = "250000"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_CellSizeBelowMin_ProducesRangeViolation()
    {
        // Handler enforces AnalyticsLimits.MinDensityCellSizeMeters (default 10).
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_CellSizeAboveMax_ProducesRangeViolation()
    {
        // Handler enforces AnalyticsLimits.MaxDensityCellSizeMeters (default 100_000).
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "500000"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_DistanceZero_IsAccepted()
    {
        // Handler accepts distance >= BufferAggregateQuery.MinDistanceMeters (0).
        // Validator must match so zero-buffer dissolves are not spuriously rejected.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "0"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty(
            "buffer-aggregate handler allows distance=0 (>= MinDistanceMeters)");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_NegativeDistance_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "-1"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_DistanceAboveMaxMeters_ProducesRangeViolation()
    {
        // Handler caps distance at AnalyticsLimits.MaxBufferDistanceMeters (default 100_000).
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "200000",
                        ["unit"] = "meters"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_DistanceCapBypassedByUnit_ProducesRangeViolation()
    {
        // A 200 km buffer exceeds the default 100 km cap once converted to
        // meters. Handler converts unit before applying MaxBufferDistanceMeters
        // so non-meter units cannot bypass the limit; validator must match.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "200",
                        ["unit"] = "km"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    // -----------------------------------------------------------------------
    // Validator — handler-parity guards (joinLayerId, outStatistics, shared filters)
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_NonIntegerJoinLayerId_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "zoning"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.joinLayerId");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_SelfJoin_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.joinLayerId");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_OutStatisticsWithoutHull_ProducesInvariantViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5",
                        ["returnHullPerCluster"] = "false",
                        ["outStatistics"] = "{\"statisticType\":\"sum\",\"onStatisticField\":\"pop\",\"outStatisticFieldName\":\"pop_total\"}"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_MalformedOutStatisticsJson_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5",
                        ["returnHullPerCluster"] = "true",
                        ["outStatistics"] = "{not-json"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_NonStringOutStatisticsField_ProducesInvalidValueViolation()
    {
        // statisticType is numeric (not a JSON string) — JsonElement.GetString()
        // would throw InvalidOperationException on this token. The validator
        // must treat it as INVALID_PARAMETER_VALUE instead of surfacing a 500.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5",
                        ["returnHullPerCluster"] = "true",
                        ["outStatistics"] = "[{\"statisticType\":1,\"onStatisticField\":\"pop\",\"outStatisticFieldName\":\"x\"}]"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_OutStatisticsUnsupportedStatisticType_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["joinLayerId"] = "200",
                        ["outStatistics"] = "[{\"statisticType\":\"median\",\"onStatisticField\":\"pop\",\"outStatisticFieldName\":\"pop_median\"}]"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregate_OutStatisticsWithoutDissolve_ProducesInvariantViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.buffer-aggregate",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["distance"] = "100",
                        ["dissolve"] = "false",
                        ["outStatistics"] = "[{\"statisticType\":\"count\",\"onStatisticField\":\"id\",\"outStatisticFieldName\":\"feature_count\"}]"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_NonNumericObjectIds_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5",
                        ["objectIds"] = "1,abc,3"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.objectIds");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_DistanceBasedSpatialRelWithGeometry_ProducesInvalidValueViolation()
    {
        // Matches AnalyticsFeatureQueryFactory: spatialRel is only rejected
        // when a geometry filter is also supplied (it is otherwise ignored).
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "100",
                        ["geometry"] = "{\"x\":0,\"y\":0}",
                        ["geometryType"] = "esriGeometryPoint",
                        ["spatialRel"] = "esriSpatialRelWithinDistance"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.spatialRel");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensity_DistanceBasedSpatialRelWithoutGeometry_IsAccepted()
    {
        // Mirrors AnalyticsFeatureQueryFactory: when no geometry is supplied
        // the handler never inspects spatialRel, so the validator must not
        // reject it either (otherwise it blocks plans execution would accept).
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.density",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["cellSize"] = "100",
                        ["spatialRel"] = "esriSpatialRelWithinDistance"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().NotContain(v =>
            v.FieldPath == "steps[s1].inputs.spatialRel");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_NonIntegerLayerId_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "parcels",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.layerId");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_ZeroLayerId_IsAccepted()
    {
        // RouteParameterValidator.ValidateLayerId accepts 0 (see
        // WebAppFixture.TestLayerId), so the catalog gate must not reject
        // zero-based layer ids the runtime would accept.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "0",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().NotContain(v =>
            v.FieldPath == "steps[s1].inputs.layerId");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_NegativeLayerId_ProducesInvalidValueViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "-1",
                        ["algorithm"] = "dbscan",
                        ["eps"] = "25",
                        ["minPoints"] = "5"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.layerId");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsSpatialJoin_ZeroJoinLayerId_IsAccepted()
    {
        // Both target and join ids use the same LayerId value type; zero-based
        // join layer ids must clear the catalog gate so plans the analytics
        // handler would accept are not blocked here.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.spatial-join",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "1",
                        ["joinLayerId"] = "0"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().NotContain(v =>
            v.FieldPath == "steps[s1].inputs.joinLayerId");
    }

    // -----------------------------------------------------------------------
    // Validator — managed analytics counterparts (#1260) mirror the executor's
    // TransformInputException checks so ValidatePlan refuses plans the executor
    // would terminally fail at runtime.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsClusterManaged_UnknownAlgorithm_ProducesEnumViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.cluster-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["algorithm"] = "fancy"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.algorithm");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsClusterManaged_DbscanWithoutEpsAndMinPoints_ProducesMissingViolations()
    {
        var plan = ManagedAnalyticsPlan("analytics.cluster-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["algorithm"] = "dbscan"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.eps");
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.minPoints");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsClusterManaged_KMeansWithoutK_ProducesMissingViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.cluster-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["algorithm"] = "kmeans"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.k");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsClusterManaged_NonPositiveEps_ProducesRangeViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.cluster-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["algorithm"] = "dbscan",
            ["eps"] = "-1",
            ["minPoints"] = "3"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.eps");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregateManaged_UnknownUnit_ProducesEnumViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.buffer-aggregate-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["distance"] = "1",
            ["unit"] = "leagues"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.unit");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsBufferAggregateManaged_NegativeDistance_ProducesRangeViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.buffer-aggregate-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["distance"] = "-5"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.distance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensityManaged_UnknownMode_ProducesEnumViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.density-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["mode"] = "triangle",
            ["cellSize"] = "10"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.mode");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensityManaged_MissingCellSize_ProducesMissingViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.density-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30="
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsDensityManaged_NonPositiveCellSize_ProducesRangeViolation()
    {
        var plan = ManagedAnalyticsPlan("analytics.density-managed", new Dictionary<string, string>
        {
            ["input"] = "data:application/geo+json;base64,e30=",
            ["cellSize"] = "0"
        });

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.cellSize");
    }

    private static AnalysisPlan ManagedAnalyticsPlan(string processId, Dictionary<string, string> inputs)
        => new()
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
                    Inputs = inputs
                }
            ]
        };

    // -----------------------------------------------------------------------
    // Catalog — immutability contract
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_ListProcesses_CannotBeCastToMutableArray()
    {
        var all = _catalog.ListProcesses();

        (all is ProcessDefinition[]).Should().BeFalse(
            "read-only catalog must not leak the underlying array through IReadOnlyList");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcessesByCategory_CannotBeCastToMutableArray()
    {
        var geometry = _catalog.GetProcessesByCategory("geometry");

        (geometry is ProcessDefinition[]).Should().BeFalse(
            "read-only catalog must not leak the underlying array through IReadOnlyList");
    }

    // -----------------------------------------------------------------------
    // Generalization family — simplify-layer
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationSimplifyLayer_MissingRequiredParameters_ProducesViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.simplify-layer",
                    Inputs = new Dictionary<string, string>()
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.layerId");
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.tolerance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationSimplifyLayer_NonPositiveTolerance_ProducesRangeViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.simplify-layer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["tolerance"] = "0"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.tolerance");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationSimplifyLayer_WithValidTolerance_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.simplify-layer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["tolerance"] = "0.001",
                        ["preserveTopology"] = "false"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationSimplifyLayer_UnknownParameter_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.simplify-layer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["tolerance"] = "0.001",
                        ["typo"] = "nope"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "UNKNOWN_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.typo");
    }

    // -----------------------------------------------------------------------
    // Generalization family — dissolve
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationDissolve_WithoutDissolveFalse_AndOutStatistics_Allowed()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.dissolve",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["outStatistics"] = "{ \"statisticType\": \"count\", \"onStatisticField\": \"id\", \"outStatisticFieldName\": \"cnt\" }"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty("dissolve defaults to true so outStatistics is allowed");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationDissolve_WithDissolveFalse_AndOutStatistics_ProducesCrossFieldViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.dissolve",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["dissolve"] = "false",
                        ["outStatistics"] = "{ \"statisticType\": \"count\", \"onStatisticField\": \"id\", \"outStatisticFieldName\": \"cnt\" }"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeneralizationDissolve_InvalidOutStatisticsJson_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "generalization.dissolve",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "100",
                        ["outStatistics"] = "not json"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.outStatistics");
    }

    // -----------------------------------------------------------------------
    // Data-management family — copy-features
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCopyFeatures_MissingRequiredParameters_ProducesViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.copy-features",
                    Inputs = new Dictionary<string, string>()
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.sourceLayerId");
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.targetLayerName");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCopyFeatures_WithRequired_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.copy-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["sourceLayerId"] = "42",
                        ["targetLayerName"] = "copy-of-parcels"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCopyFeatures_InvalidObjectIds_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.copy-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["sourceLayerId"] = "42",
                        ["targetLayerName"] = "copy",
                        ["objectIds"] = "1, two, 3"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.objectIds");
    }

    // -----------------------------------------------------------------------
    // Data-management family — delete-features (destructive)
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementDeleteFeatures_WithoutFilter_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.delete-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.where");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementDeleteFeatures_WithWhere_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.delete-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["where"] = "status = 'retired'"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementDeleteFeatures_WithObjectIds_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.delete-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["objectIds"] = "1,2,3"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Data-management family — calculate-field (destructive)
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_MissingRequiredParameters_ProducesViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.fieldName");
        violations.Should().Contain(v =>
            v.Code == "MISSING_REQUIRED_PARAMETER" &&
            v.FieldPath == "steps[s1].inputs.expression");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_ValidInputs_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["fieldName"] = "status_code",
                        ["expression"] = "42"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_InvalidFieldName_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["fieldName"] = "bad field-name",
                        ["expression"] = "1"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.fieldName");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_NonAsciiFieldName_ProducesViolation()
    {
        // The storage-engine field-name regex is strict ASCII
        // (FeatureQueryBuilder.ValidFieldNameRegex / DuckDB FieldNameRegex).
        // Validator must reject non-ASCII identifiers (e.g. `Åfield`) that
        // char.IsLetter would otherwise admit, so plans accepted here are
        // also accepted by the live field-name binders.
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["fieldName"] = "\u00c5field",
                        ["expression"] = "1"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.fieldName");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCopyFeatures_BlankTargetLayerName_ProducesViolation()
    {
        // Declared-required Text inputs must be rejected when blank so the
        // validator does not admit plans the handlers would treat as missing
        // (handlers use IsNullOrWhiteSpace for "not supplied").
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.copy-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["sourceLayerId"] = "42",
                        ["targetLayerName"] = "   "
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.targetLayerName");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_BlankFieldName_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["fieldName"] = "",
                        ["expression"] = "1"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.fieldName");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_DataManagementCalculateField_BlankExpression_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.calculate-field",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "42",
                        ["fieldName"] = "status_code",
                        ["expression"] = "\t"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.expression");
    }

    // -----------------------------------------------------------------------
    // Destructive-process classifier
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_DeleteFeatures_IsDestructive()
    {
        ProcessDestructiveClassifier.IsDestructive("data-management.delete-features")
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_CalculateField_IsDestructive()
    {
        ProcessDestructiveClassifier.IsDestructive("data-management.calculate-field")
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_CopyFeatures_IsNotDestructive()
    {
        ProcessDestructiveClassifier.IsDestructive("data-management.copy-features")
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_AnalyticsProcess_IsNotDestructive()
    {
        ProcessDestructiveClassifier.IsDestructive("analytics.cluster")
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_PlanWithDeleteFeatures_ReturnsProcessId()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.delete-features"
                }
            ]
        };

        ProcessDestructiveClassifier.FindFirstDestructiveProcessId(plan)
            .Should().Be("data-management.delete-features");
        ProcessDestructiveClassifier.HasDestructiveStep(plan).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void DestructiveClassifier_PlanWithoutDestructiveStep_ReturnsNull()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.copy-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["sourceLayerId"] = "42",
                        ["targetLayerName"] = "copy"
                    }
                }
            ]
        };

        ProcessDestructiveClassifier.FindFirstDestructiveProcessId(plan).Should().BeNull();
        ProcessDestructiveClassifier.HasDestructiveStep(plan).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Integration: ValidatePlan RPC with catalog validation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithUnknownProcessId_ReturnsNotExecutable()
    {
        var sut = CreateServiceWithCatalog();

        var plan = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1",
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };
        var step = new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess"
        };
        step.Inputs["processId"] = ToProtoParameterValue("nonexistent.op");
        plan.Steps.Add(step);

        var response = await sut.ValidatePlan(
            new Proto.ValidatePlanRequest { Plan = plan },
            CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().Contain(v => v.Message.Contains("not in the catalog", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithKnownProcess_MissingRequiredParam_ReturnsNotExecutable()
    {
        var sut = CreateServiceWithCatalog();

        var plan = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1",
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };
        var step = new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess"
        };
        step.Inputs["processId"] = ToProtoParameterValue("geometry.buffer");
        plan.Steps.Add(step);

        var response = await sut.ValidatePlan(
            new Proto.ValidatePlanRequest { Plan = plan },
            CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().Contain(v => v.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AnalysisPlan CreateSingleStepPlan(string processId, Dictionary<string, string> inputs) => new()
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
                Inputs = inputs
            }
        ]
    };

    private HonuaProcessService CreateServiceWithCatalog()
    {
        var authEval = Substitute.For<IOperatorAuthorizationEvaluator>();
        authEval.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));

        var approvalEval = Substitute.For<IOperatorApprovalEvaluator>();
        approvalEval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        var jobService = new GeoprocessingJobService(
            Substitute.For<IUniversalProgressStore>(),
            [Substitute.For<IJobCancellationNotifier>()],
            authEval,
            approvalEval,
            _catalog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GeoprocessingJobService>.Instance);

        return new HonuaProcessService(
            jobService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaProcessService>.Instance);
    }

    private static Proto.ParameterValue ToProtoParameterValue(string value)
        => new() { StringValue = value };

    private static TestServerCallContext CreateCallContext()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")], "Test"))
        };

        var ctx = new TestServerCallContext();
        ctx.UserState["__HttpContext"] = httpContext;
        return ctx;
    }

    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Metadata _responseTrailers = new();

        public void Dispose() => _cts.Dispose();

        protected override string MethodCore => "/geospatial.v1.ProcessService/ValidatePlan";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
