// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class RasterExecutionPlannerTests
{
    private readonly RasterExecutionPlanner _sut = new(
        CreateRegistry(),
        NullLogger<RasterExecutionPlanner>.Instance);
    private readonly RasterExecutionPlanner _builtInPlanner = new(
        new RasterEngineCapabilityRegistry(),
        NullLogger<RasterExecutionPlanner>.Instance);

    [Fact]
    public void Plan_DataResidentBoundedWork_SelectsDurablePostgis()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 32 * MiB, scratchBytes: 64 * MiB, databaseWork: 2_000_000)));

        decision.Engine.Should().Be(RasterEngine.Postgis);
        decision.Placement.Should().Be(RasterExecutionPlacement.DurablePostgis);
        decision.ReasonCode.Should().Be("postgis-source-local");
        decision.PolicyRef.Should().Be("test-policy");
    }

    [Fact]
    public void Plan_RequestEligibleDataResidentWork_SelectsBoundedPostgisRequest()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            AllowRequestExecution = true,
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.Postgis);
        decision.Placement.Should().Be(RasterExecutionPlacement.Request);
        decision.ReasonCode.Should().Be("postgis-request-budget");
    }

    [Fact]
    public void Plan_ObjectStoreWork_SelectsRemoteNativeBackend()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 256 * MiB, databaseWork: 5_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.Backend.Should().Be("aws-batch");
        decision.ReasonCode.Should().Be("native-remote-source-local");
    }

    [Fact]
    public void Plan_ModestInlineWork_SelectsLocalNativeWorker()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
        decision.ReasonCode.Should().Be("native-local-budget");
    }

    [Fact]
    public void Plan_HighScratchInlineWork_SelectsRemoteNativeBackend()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 512 * MiB, databaseWork: 5_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-burst-isolation");
    }

    [Fact]
    public void Plan_DatabasePressure_SelectsSemanticallyCapableNativeWorker()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(database: RasterDatabaseHealth.Pressured),
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_DatabaseBudgetExceeded_SelectsSemanticallyCapableNativeWorker()
    {
        var decision = _sut.Plan(Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 600_000_000)));

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_UnknownCostWithoutRemoteBackend_RefusesActionably()
    {
        var request = Request(
            RasterInputResidency.Inline,
            new RasterCostEstimatorInput()) with
        {
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => exception.ReasonCode == "no-eligible-raster-placement")
            .WithMessage("*incomplete*remote backend*");
    }

    [Fact]
    public void Plan_ExternalSourceWithoutRemoteBackend_RefusesActionably()
    {
        var request = Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .WithMessage("*external raster sources*remote native backend*");
    }

    [Fact]
    public void Plan_RequiredUnavailablePlacement_DoesNotSilentlyOverridePolicy()
    {
        var request = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(requiredPlacement: RasterExecutionPlacement.RemoteBackend),
            Health = Health(remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => exception.ReasonCode == "no-eligible-raster-placement");
    }

    [Fact]
    public void Plan_EngineDisabledByPolicy_SelectsAllowedEngineOnly()
    {
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(allowedEngines: [RasterEngine.GdalNative]),
        };

        var decision = _sut.Plan(request);

        decision.Engine.Should().Be(RasterEngine.GdalNative);
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void Plan_SameImmutableSnapshots_IsIdempotent()
    {
        var request = Request(
            RasterInputResidency.ObjectStoreCog,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 256 * MiB, databaseWork: 5_000_000));

        _sut.Plan(request).Should().BeEquivalentTo(_sut.Plan(request));
    }

    [Fact]
    public void Plan_MutatingRetry_ReusesPinnedDecisionDespiteChangedHealth()
    {
        var initial = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));
        var retry = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            ExistingDecision = initial,
            MutatingAttemptStarted = true,
            Health = Health(localAvailable: false, remoteAvailable: true),
            Policy = Policy(requiredPlacement: RasterExecutionPlacement.RemoteBackend),
        };

        _sut.Plan(retry).Should().BeSameAs(initial);
    }

    [Fact]
    public void Plan_MutatingRetryWithInvalidPinnedPlacement_RejectsBeforeReuse()
    {
        var initial = _sut.Plan(Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)));
        var retry = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            ExistingDecision = initial with { Placement = RasterExecutionPlacement.DurablePostgis },
            MutatingAttemptStarted = true,
        };

        var act = () => _sut.Plan(retry);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*invalid engine/placement binding*");
    }

    [Fact]
    public void Plan_RegistryCapabilityFailure_IsReportedBeforePlacement()
    {
        var planner = new RasterExecutionPlanner(
            new RasterEngineCapabilityRegistry(),
            NullLogger<RasterExecutionPlanner>.Instance);
        var request = Request(
            RasterInputResidency.Postgis,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Policy = Policy(requiredEngine: RasterEngine.Postgis),
        };

        var act = () => planner.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => !exception.IsRetryable)
            .WithMessage("*No canonical PostGIS raster IProcessExecutor*");
    }

    [Fact]
    public void Plan_TransientWorkerHealthFailure_IsRetryable()
    {
        var request = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(localAvailable: false, remoteAvailable: false),
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => exception.IsRetryable)
            .WithMessage("*no allowed available placement*");
    }

    [Fact]
    public void Plan_UnhealthyLocalWorkerCannotRecoverOversizedRequest_IsNotRetryable()
    {
        var request = Request(
            RasterInputResidency.Inline,
            Cost(decodedBytes: 128 * MiB, scratchBytes: 256 * MiB, databaseWork: 100_000)) with
        {
            Health = Health(localAvailable: false, remoteAvailable: false),
            Policy = Policy() with
            {
                AllowedPlacements = [RasterExecutionPlacement.LocalNativeWorker],
            },
        };

        var act = () => _sut.Plan(request);

        act.Should().Throw<RasterExecutionPlanningException>()
            .Where(exception => !exception.IsRetryable)
            .WithMessage("*no allowed available placement fits the local-worker budget*");
    }

    [Fact]
    public void Plan_UndefinedSnapshotEnum_RejectsBeforeCapabilityLookup()
    {
        var request = Request(
            (RasterInputResidency)999,
            Cost(decodedBytes: 8 * MiB, scratchBytes: 16 * MiB, databaseWork: 100_000));

        var act = () => _sut.Plan(request);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*undefined enum value*");
    }

    [Fact]
    public void RequestFactory_LegacyCatalogReference_ClassifiesEventualInlineResidencyWithoutMaterializing()
    {
        var process = CreateRegistry().Find("raster.clip")!;
        var plan = new AnalysisPlan
        {
            PlanId = "plan-catalog-cog",
            IntentId = "intent-catalog-cog",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "raster.clip",
                    Inputs = new Dictionary<string, string> { ["rasterId"] = "42" },
                },
            ],
        };

        var request = RasterExecutionPlanningRequestFactory.Create(
            plan,
            process,
            new RasterExecutionPlannerOptions(),
            remoteBackendAvailable: false,
            remoteBackend: null);

        request.InputResidencies.Should().Equal(RasterInputResidency.Inline);
        request.Cost.DecodedBytes.Should().BeNull();
        request.Cost.ExpectedScratchBytes.Should().BeNull();
        plan.Steps[0].Inputs.Should().NotContainKey("source");
    }

    [Fact]
    public void RequestFactory_AlternateLegacyRasterInputs_ProduceCompleteLocalNativeEstimates()
    {
        var smallTiff = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1);
        AssertLegacyInputsUseLocal(
            "raster.map-algebra",
            new Dictionary<string, string>
            {
                ["sources"] = $"{smallTiff}|{smallTiff}",
                ["expression"] = "A+B",
            },
            expectedSourceCount: 2);
        AssertLegacyInputsUseLocal(
            "raster.spectral-index",
            new Dictionary<string, string>
            {
                ["index"] = "NDVI",
                ["nir"] = smallTiff,
                ["red"] = smallTiff,
            },
            expectedSourceCount: 2);
        AssertLegacyInputsUseLocal(
            "raster.interpolate-idw",
            new Dictionary<string, string>
            {
                ["points"] = "e30=",
            },
            expectedSourceCount: 1);
    }

    [Fact]
    public void RequestFactory_MapAlgebra_SizesSingleBandOutputOnFirstSourceGrid()
    {
        var source = CreateTiffHeaderBase64(
            width: 32,
            height: 16,
            bands: 1,
            bitsPerSample: 8);
        var request = CreateLegacyRequest(
            "raster.map-algebra",
            new Dictionary<string, string>
            {
                ["sources"] = string.Join('|', source, source, source, source),
                ["expression"] = "A+B+C+D",
                ["dataType"] = "Byte",
            });

        request.Cost.SourceCount.Should().Be(4);
        request.Cost.BandCount.Should().Be(4, "input work still scans four source bands");
        request.Cost.InputPixels.Should().Be(2_048);
        request.Cost.OutputPixels.Should().Be(512, "gdal_calc emits one first-source-sized grid");
        request.Cost.DecodedBytes.Should().Be(2_560, "four inputs and one output are resident");
        request.Cost.ExpectedScratchBytes.Should().Be(5_120);
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_CalcRequestedFloat64_ChargesEightByteOutputSamples()
    {
        var source = CreateTiffHeaderBase64(
            width: 100,
            height: 50,
            bands: 1,
            bitsPerSample: 8);
        var mapAlgebra = CreateLegacyRequest(
            "raster.map-algebra",
            new Dictionary<string, string>
            {
                ["sources"] = $"{source}|{source}",
                ["expression"] = "A+B",
                ["dataType"] = "Float64",
            });
        var reclassify = CreateLegacyRequest(
            "raster.reclassify",
            new Dictionary<string, string>
            {
                ["source"] = source,
                ["remap"] = "0..10:1",
                ["dataType"] = "Float64",
            });

        mapAlgebra.Cost.OutputPixels.Should().Be(5_000);
        mapAlgebra.Cost.DecodedBytes.Should().Be(50_000,
            "two 5,000-byte inputs plus one 40,000-byte Float64 output are resident");
        reclassify.Cost.DecodedBytes.Should().Be(45_000,
            "one 5,000-byte input plus one 40,000-byte Float64 output are resident");
    }

    [Fact]
    public void RequestFactory_SpectralIndex_ChargesFixedFloat32SingleBandOutput()
    {
        var source = CreateTiffHeaderBase64(
            width: 100,
            height: 50,
            bands: 1,
            bitsPerSample: 8);
        var request = CreateLegacyRequest(
            "raster.spectral-index",
            new Dictionary<string, string>
            {
                ["index"] = "NDVI",
                ["nir"] = source,
                ["red"] = source,
            });

        request.Cost.BandCount.Should().Be(2, "input work scans both band-role rasters");
        request.Cost.InputPixels.Should().Be(10_000);
        request.Cost.OutputPixels.Should().Be(5_000);
        request.Cost.DecodedBytes.Should().Be(30_000,
            "two byte inputs plus the fixed Float32 output are resident");
    }

    [Fact]
    public void RequestFactory_MosaicWithoutTrustedUnionGrid_KeepsOutputConservativeAndOffloads()
    {
        var smallTiff = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1);
        var request = CreateLegacyRequest(
            "raster.mosaic",
            new Dictionary<string, string>
            {
                ["sources"] = $"{smallTiff}|{smallTiff}",
                ["operator"] = "last",
            },
            remoteBackendAvailable: true);

        request.Cost.SourceCount.Should().Be(2);
        request.Cost.InputPixels.Should().Be(1_024);
        request.Cost.OutputPixels.Should().BeNull(
            "input dimensions cannot bound the union grid without trusted georeferencing metadata");
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-conservative");
        decision.Cost.UnknownInputs.Should().Contain("outputPixels");
    }

    [Fact]
    public void RequestFactory_ZonalStatistics_DerivesBoundedZoneCountFromAcceptedZonesPayload()
    {
        const string zonesJson =
            """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":null,"properties":{}},{"type":"Feature","geometry":null,"properties":{}}]}""";
        var zonesBytes = Encoding.UTF8.GetBytes(zonesJson);
        var encodedZones = Convert.ToBase64String(zonesBytes);
        var admittedZoneBytes = encodedZones.Length * 3L / 4;
        var request = CreateLegacyRequest(
            "raster.zonal-statistics",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1),
                ["zones"] = encodedZones,
            });

        request.Cost.ZoneCount.Should().Be(2,
            "managed bounded metadata parsing counts the actual FeatureCollection entries");
        request.Cost.DecodedBytes.Should().Be(4_096 + (admittedZoneBytes * 4),
            "the raster allocation plus zones payload and parsing footprint count toward admission");
        request.Cost.ExpectedScratchBytes.Should().Be((4_096 + (admittedZoneBytes * 4)) * 2);
        request.Cost.ExpectedDatabaseWork.Should().Be(1_024,
            "each actual zone can scan the source raster");
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_LargeZonalPayload_DoesNotMaterializeGeoJsonInWebProcess()
    {
        var zonesJson =
            "{\"type\":\"FeatureCollection\",\"features\":[],\"padding\":\""
            + new string('x', (1024 * 1024) + 1)
            + "\"}";
        var request = CreateLegacyRequest(
            "raster.zonal-statistics",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1),
                ["zones"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(zonesJson)),
            },
            remoteBackendAvailable: true);

        request.Cost.ZoneCount.Should().BeNull(
            "large GeoJSON metadata must not be materialized by the lightweight web process");
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-conservative");
    }

    [Fact]
    public void RequestFactory_NonStringGeoJsonType_RemainsConservative()
    {
        var request = CreateLegacyRequest(
            "raster.zonal-statistics",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1),
                ["zones"] = Convert.ToBase64String(
                    """{"type":1,"features":[]}"""u8.ToArray()),
            },
            remoteBackendAvailable: true);

        request.Cost.ZoneCount.Should().BeNull();
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-conservative");
    }

    [Fact]
    public void RequestFactory_RasterClip_ChargesBoundaryPayloadAndParsingFootprint()
    {
        var boundaryBytes = new byte[4_096];
        var encodedBoundary = Convert.ToBase64String(boundaryBytes);
        var admittedBoundaryBytes = encodedBoundary.Length * 3L / 4;
        var request = CreateLegacyRequest(
            "raster.clip",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1),
                ["boundary"] = encodedBoundary,
            });

        request.Cost.DecodedBytes.Should().Be(4_096 + (admittedBoundaryBytes * 4),
            "the boundary WKB and its managed geometry parsing footprint are local allocations");
        request.Cost.ExpectedScratchBytes.Should().Be((4_096 + (admittedBoundaryBytes * 4)) * 2);
    }

    [Fact]
    public void RequestFactory_HighlyCompressedTiff_UsesHeaderDimensionsAndOffloads()
    {
        var request = CreateLegacyRequest(
            "gdal.gdalwarp",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(
                    width: 20_000,
                    height: 20_000,
                    bands: 1,
                    bitsPerSample: 128),
                ["targetSrs"] = "3857",
            },
            remoteBackendAvailable: true);

        request.Cost.InputPixels.Should().Be(400_000_000);
        request.Cost.DecodedBytes.Should().Be(6_400_000_000);
        request.Cost.ExpectedScratchBytes.Should().Be(12_800_000_000);
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
    }

    [Fact]
    public void RequestFactory_ByteTiff_HonorsDeclaredSampleWidthForLocalPlacement()
    {
        var request = CreateLegacyRequest(
            "raster.statistics",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(
                    width: 20_000,
                    height: 20_000,
                    bands: 1,
                    bitsPerSample: 8),
            });

        request.Cost.InputPixels.Should().Be(400_000_000);
        request.Cost.DecodedBytes.Should().Be(400_000_000);
        request.Cost.ExpectedScratchBytes.Should().Be(800_000_000);
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_ReprojectWithoutTrustedTargetGrid_KeepsOutputConservativeAndOffloads()
    {
        var request = CreateLegacyRequest(
            "raster.reproject",
            new Dictionary<string, string>
            {
                ["source"] = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1),
                ["targetSrid"] = "3857",
            },
            remoteBackendAvailable: true);

        request.Cost.InputPixels.Should().Be(512);
        request.Cost.OutputPixels.Should().BeNull(
            "a target CRS can produce a materially different grid even when source dimensions are known");
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-conservative");
        decision.Cost.UnknownInputs.Should().Contain("outputPixels");
    }

    [Fact]
    public void RequestFactory_UnrecognizedInlineRaster_DoesNotTrustEncodedSizeMultiplier()
    {
        var request = CreateLegacyRequest(
            "gdal.gdalwarp",
            new Dictionary<string, string>
            {
                ["source"] = "AAAA",
                ["targetSrs"] = "3857",
            },
            remoteBackendAvailable: true);

        request.Cost.DecodedBytes.Should().BeNull();
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.ReasonCode.Should().Be("native-remote-conservative");
    }

    [Fact]
    public void RequestFactory_RasterizeCellSize_KeepsDerivedGridConservativeAndOffloads()
    {
        const string geoJson = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[0,0],[10000,0],[10000,10000],[0,10000],[0,0]]]}}]}
            """;
        var request = CreateLegacyRequest(
            "conversion.rasterize",
            new Dictionary<string, string>
            {
                ["source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson)),
                ["burnValue"] = "1",
                ["cellSize"] = "0.01",
            },
            remoteBackendAvailable: true);

        request.Cost.OutputPixels.Should().BeNull();
        var decision = _builtInPlanner.Plan(request);
        decision.Placement.Should().Be(RasterExecutionPlacement.RemoteBackend);
        decision.Cost.UnknownInputs.Should().Contain("outputPixels");
    }

    [Fact]
    public void RequestFactory_ResamplePixelScale_DerivesBoundedOutputGridForLocalWorker()
    {
        var request = CreateLegacyRequest(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = CreateGeoTiffHeaderBase64(
                    width: 1_000,
                    height: 500,
                    scaleX: 30,
                    scaleY: 20),
                ["cellSize"] = "60",
                ["cellSizeY"] = "10",
            });

        request.Cost.InputPixels.Should().Be(500_000);
        request.Cost.OutputPixels.Should().Be(500_000);
        var decision = _builtInPlanner.Plan(request);
        decision.Cost.UnknownInputs.Should().BeEmpty();
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_ResampleModelTransformation_DerivesBoundedOutputGridForLocalWorker()
    {
        var request = CreateLegacyRequest(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = CreateTransformedGeoTiffHeaderBase64(
                    width: 1_000,
                    height: 500,
                    xFromColumn: 30,
                    xFromRow: 0,
                    yFromColumn: 0,
                    yFromRow: -20),
                ["cellSize"] = "60",
                ["cellSizeY"] = "10",
            });

        request.Cost.InputPixels.Should().Be(500_000);
        request.Cost.OutputPixels.Should().Be(500_000);
        var decision = _builtInPlanner.Plan(request);
        decision.Cost.UnknownInputs.Should().BeEmpty();
        decision.Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_ResampleWithLateFirstIfd_ReadsOnlyDeclaredMetadataRanges()
    {
        var request = CreateLegacyRequest(
            "raster.resample",
            new Dictionary<string, string>
            {
                ["source"] = CreateGeoTiffHeaderBase64(
                    width: 1_000,
                    height: 500,
                    scaleX: 30,
                    scaleY: 20,
                    ifdOffset: 70 * 1024),
                ["cellSize"] = "60",
                ["cellSizeY"] = "10",
            });

        request.Cost.InputPixels.Should().Be(500_000);
        request.Cost.OutputPixels.Should().Be(500_000);
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    [Fact]
    public void RequestFactory_MimeBase64Whitespace_MatchesWorkerDecoder()
    {
        var source = CreateTiffHeaderBase64(width: 32, height: 16, bands: 1);
        var mimeSource = string.Join(
            "\r\n",
            Enumerable.Range(0, (source.Length + 7) / 8)
                .Select(index => source.Substring(index * 8, Math.Min(8, source.Length - index * 8))));
        var request = CreateLegacyRequest(
            "raster.statistics",
            new Dictionary<string, string> { ["source"] = mimeSource });

        request.Cost.InputPixels.Should().Be(512);
        request.Cost.DecodedBytes.Should().Be(4_096);
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    private void AssertLegacyInputsUseLocal(
        string processId,
        Dictionary<string, string> inputs,
        long expectedSourceCount)
    {
        var request = CreateLegacyRequest(processId, inputs);

        request.Cost.SourceCount.Should().Be(expectedSourceCount);
        request.Cost.BandCount.Should().NotBeNull();
        request.Cost.InputPixels.Should().NotBeNull();
        request.Cost.OutputPixels.Should().NotBeNull();
        request.Cost.DecodedBytes.Should().NotBeNull();
        request.Cost.ExpectedScratchBytes.Should().NotBeNull();
        request.Cost.ExpectedDatabaseWork.Should().NotBeNull();
        _builtInPlanner.Plan(request).Placement.Should().Be(RasterExecutionPlacement.LocalNativeWorker);
    }

    private static RasterExecutionPlanningRequest CreateLegacyRequest(
        string processId,
        Dictionary<string, string> inputs,
        bool remoteBackendAvailable = false)
    {
        var process = new RasterEngineCapabilityRegistry().Find(processId)!;
        var plan = new AnalysisPlan
        {
            PlanId = "plan-" + processId,
            IntentId = "intent-" + processId,
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = inputs,
                },
            ],
        };

        return RasterExecutionPlanningRequestFactory.Create(
            plan,
            process,
            new RasterExecutionPlannerOptions(),
            remoteBackendAvailable,
            remoteBackend: remoteBackendAvailable ? "aws-batch" : null);
    }

    private static string CreateTiffHeaderBase64(
        int width,
        int height,
        int bands,
        int bitsPerSample = 64)
    {
        var payload = new byte[62];
        payload[0] = (byte)'I';
        payload[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 4);
        WriteTiffEntry(payload, 10, tag: 256, type: 4, value: (uint)width);
        WriteTiffEntry(payload, 22, tag: 257, type: 4, value: (uint)height);
        WriteTiffEntry(payload, 34, tag: 258, type: 3, value: (uint)bitsPerSample);
        WriteTiffEntry(payload, 46, tag: 277, type: 3, value: (uint)bands);
        return Convert.ToBase64String(payload);
    }

    private static string CreateGeoTiffHeaderBase64(
        int width,
        int height,
        double scaleX,
        double scaleY,
        int ifdOffset = 8)
    {
        var pixelScaleOffset = checked(ifdOffset + 66);
        var payload = new byte[pixelScaleOffset + sizeof(double) * 3];
        payload[0] = (byte)'I';
        payload[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), checked((uint)ifdOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset), 5);
        WriteTiffEntry(payload, ifdOffset + 2, tag: 256, type: 4, value: (uint)width);
        WriteTiffEntry(payload, ifdOffset + 14, tag: 257, type: 4, value: (uint)height);
        WriteTiffEntry(payload, ifdOffset + 26, tag: 258, type: 3, value: 64);
        WriteTiffEntry(payload, ifdOffset + 38, tag: 277, type: 3, value: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset + 50), 33550);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset + 52), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(ifdOffset + 54), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(ifdOffset + 58), checked((uint)pixelScaleOffset));
        WriteDouble(payload, pixelScaleOffset, scaleX);
        WriteDouble(payload, pixelScaleOffset + sizeof(double), scaleY);
        WriteDouble(payload, pixelScaleOffset + sizeof(double) * 2, 0);
        return Convert.ToBase64String(payload);
    }

    private static string CreateTransformedGeoTiffHeaderBase64(
        int width,
        int height,
        double xFromColumn,
        double xFromRow,
        double yFromColumn,
        double yFromRow)
    {
        const int ifdOffset = 8;
        const int matrixOffset = ifdOffset + 66;
        var payload = new byte[matrixOffset + sizeof(double) * 16];
        payload[0] = (byte)'I';
        payload[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset), 5);
        WriteTiffEntry(payload, ifdOffset + 2, tag: 256, type: 4, value: (uint)width);
        WriteTiffEntry(payload, ifdOffset + 14, tag: 257, type: 4, value: (uint)height);
        WriteTiffEntry(payload, ifdOffset + 26, tag: 258, type: 3, value: 64);
        WriteTiffEntry(payload, ifdOffset + 38, tag: 277, type: 3, value: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset + 50), 34264);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ifdOffset + 52), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(ifdOffset + 54), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(ifdOffset + 58), matrixOffset);
        WriteDouble(payload, matrixOffset, xFromColumn);
        WriteDouble(payload, matrixOffset + sizeof(double), xFromRow);
        WriteDouble(payload, matrixOffset + 4 * sizeof(double), yFromColumn);
        WriteDouble(payload, matrixOffset + 5 * sizeof(double), yFromRow);
        WriteDouble(payload, matrixOffset + 10 * sizeof(double), 1);
        WriteDouble(payload, matrixOffset + 15 * sizeof(double), 1);
        return Convert.ToBase64String(payload);
    }

    private static void WriteDouble(byte[] payload, int offset, double value)
        => BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(offset),
            unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    private static void WriteTiffEntry(
        byte[] payload,
        int offset,
        ushort tag,
        ushort type,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 8), value);
    }

    private const long MiB = 1024L * 1024L;

    private static RasterExecutionPlanningRequest Request(
        RasterInputResidency residency,
        RasterCostEstimatorInput cost) => new()
        {
            ProcessId = "raster.clip",
            InputResidencies = [residency],
            InputMediaTypes = ["image/tiff"],
            OutputSink = RasterOutputSink.JobArtifact,
            Cost = cost,
            Budgets = Budgets(),
            Health = Health(),
            Policy = Policy(),
        };

    private static RasterCostEstimatorInput Cost(long decodedBytes, long scratchBytes, long databaseWork) => new()
    {
        SourceCount = 1,
        BandCount = 1,
        ZoneCount = 0,
        InputPixels = decodedBytes / 8,
        OutputPixels = decodedBytes / 8,
        DecodedBytes = decodedBytes,
        ExpectedScratchBytes = scratchBytes,
        ExpectedDatabaseWork = databaseWork,
    };

    private static RasterExecutionBudgetSnapshot Budgets() => new()
    {
        Version = "config-v1",
        MaxRequestDecodedBytes = 64 * MiB,
        MaxRequestScratchBytes = 128 * MiB,
        MaxRequestDatabaseWork = 10_000_000,
        MaxDatabaseDecodedBytes = 512 * MiB,
        MaxDatabaseScratchBytes = 1024 * MiB,
        MaxDatabaseWork = 500_000_000,
        MaxLocalDecodedBytes = 64 * MiB,
        MaxLocalScratchBytes = 128 * MiB,
    };

    private static RasterExecutionHealthSnapshot Health(
        RasterDatabaseHealth database = RasterDatabaseHealth.Healthy,
        bool localAvailable = true,
        bool remoteAvailable = true) => new()
        {
            Version = "health-v1",
            Database = database,
            LocalNativeWorkerAvailable = localAvailable,
            RemoteNativeBackendAvailable = remoteAvailable,
            RemoteBackend = remoteAvailable ? "aws-batch" : null,
        };

    private static RasterExecutionPolicySnapshot Policy(
        RasterEngine? requiredEngine = null,
        RasterExecutionPlacement? requiredPlacement = null,
        IReadOnlyList<RasterEngine>? allowedEngines = null) => new()
        {
            PolicyRef = "test-policy",
            AllowedEngines = allowedEngines ?? [RasterEngine.Postgis, RasterEngine.GdalNative],
            AllowedPlacements =
            [
                RasterExecutionPlacement.Request,
                RasterExecutionPlacement.DurablePostgis,
                RasterExecutionPlacement.LocalNativeWorker,
                RasterExecutionPlacement.RemoteBackend,
            ],
            RequiredEngine = requiredEngine,
            RequiredPlacement = requiredPlacement,
        };

    private static RasterEngineCapabilityRegistry CreateRegistry() =>
        new RasterEngineCapabilityRegistry(
        [
            new RasterProcessCapability
            {
                ProcessId = "raster.clip",
                SemanticVersion = "1.0.0",
                Engines =
                [
                    Capability(
                        RasterEngine.Postgis,
                        RasterEngineDefaultPreference.Preferred,
                        [RasterInputResidency.Postgis]),
                    Capability(
                        RasterEngine.GdalNative,
                        RasterEngineDefaultPreference.Fallback,
                        [
                            RasterInputResidency.Postgis,
                            RasterInputResidency.ObjectStoreCog,
                            RasterInputResidency.StagedArtifact,
                            RasterInputResidency.Inline,
                        ]),
                ],
            },
        ]);

    private static RasterEngineCapability Capability(
        RasterEngine engine,
        RasterEngineDefaultPreference preference,
        IReadOnlyList<RasterInputResidency> residencies) => new()
        {
            Engine = engine,
            ImplementationVersion = $"test.{engine}@1.0.0",
            RequiredCapabilities = ["raster.clip"],
            Formats = new RasterFormatRestrictions
            {
                InputMediaTypes = ["image/tiff"],
                OutputMediaTypes = ["image/tiff"],
            },
            InputResidencies = residencies,
            OutputSinks = [RasterOutputSink.JobArtifact],
            RequestExecutionAllowed = engine == RasterEngine.Postgis,
            DefaultPreference = preference,
            IsAvailable = true,
        };
}
