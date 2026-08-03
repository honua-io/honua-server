// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgisRasterOperationCapabilityMatrixTests
{
    [UnitTest]
    public void Rows_MatchCanonicalRast016SemanticVariants()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["raster.clip"] = ["default", "pixel-center"],
            ["raster.reproject"] = ["default", "nearest", "bilinear", "cubic", "cubicspline", "lanczos", "antimeridian", "invalid-crs"],
            ["raster.resample"] = ["default", "nearest", "bilinear", "cubic", "cubicspline", "lanczos"],
            ["raster.mosaic"] = ["default", "first", "last", "min", "max", "mean", "cancellation"],
            ["raster.map-algebra"] = ["default", "allowlisted-expression", "a-plus-b", "multiband-promotion"],
            ["raster.reclassify"] = ["default", "closed-open"],
            ["raster.spectral-index"] = ["default", "ndvi", "ndwi", "ndbi", "evi", "savi"],
            ["raster.statistics"] = ["default", "population", "empty-input"],
            ["raster.histogram"] = ["default", "equal-width"],
            ["surface.roughness"] = ["default", "three-by-three"],
            ["surface.rugosity-tri"] = ["default", "three-by-three"],
            ["surface.rugosity-tpi"] = ["default", "three-by-three"],
        };

        var actual = PostgisRasterOperationCapabilityMatrix.Rows
            .GroupBy(row => row.ProcessId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.SemanticVariantId).ToArray(),
                StringComparer.Ordinal);

        actual.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (processId, variants) in expected)
        {
            actual[processId].Should().Equal(variants, processId);
        }
    }

    [UnitTest]
    public void Rows_PinPostgis34AndRasterExtensionWithoutWebNativeDependencies()
    {
        PostgisRasterOperationCapabilityMatrix.Rows.Should().OnlyContain(row =>
            row.ProviderId == "postgis"
            && row.Engine == RasterEngine.Postgis
            && row.MinimumRuntimeVersion == "3.4.0"
            && row.RequiredExtensions.Count == 1
            && row.RequiredExtensions[0].ExtensionName == "postgis_raster"
            && row.RequiredExtensions[0].MinimumVersion == "3.4.0");

        PostgisRasterOperationCapabilityMatrix.Rows
            .SelectMany(row => row.ServingPrimitives)
            .Should().NotContain(primitive =>
                primitive.Contains("GDAL", StringComparison.OrdinalIgnoreCase)
                || primitive.Contains("byte", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Rows_DistinguishExistingServingPathsFromProviderLibraryOnlySemantics()
    {
        Find("raster.clip", "pixel-center").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
        Find("raster.reclassify", "closed-open").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.ProviderLibraryOnly);
        Find("raster.map-algebra", "allowlisted-expression").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.ProviderLibraryOnly);
        Find("raster.spectral-index", "ndvi").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
        Find("raster.spectral-index", "ndwi").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
        Find("raster.spectral-index", "savi").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
        Find("raster.spectral-index", "ndbi").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.ProviderLibraryOnly);
        Find("raster.spectral-index", "evi").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.ProviderLibraryOnly);
        Find("surface.roughness", "three-by-three").ServingPrimitiveStatus
            .Should().Be(RasterServingPrimitiveStatus.HonuaServingPath);
    }

    [UnitTest]
    public void Rows_ReuseStableRast016FixtureIdentifiersAndExposeUnassignedProofGaps()
    {
        Find("raster.clip", "default").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.clip", "pixel-center").RequiredFixtureIds
            .Should().Equal("clip.pixel-center-boundary.v1");
        Find("raster.reproject", "nearest").RequiredFixtureIds
            .Should().Equal("reproject.nearest-grid.v1");
        Find("raster.reproject", "bilinear").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.resample", "bilinear").RequiredFixtureIds
            .Should().Equal("resample.bilinear-nodata-edge.v1");
        Find("raster.mosaic", "last").RequiredFixtureIds
            .Should().Equal("mosaic.last-overlap-nodata.v1");
        Find("raster.map-algebra", "multiband-promotion").RequiredFixtureIds
            .Should().Equal("multiband.promotion-color.v1");
        Find("raster.map-algebra", "default").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.spectral-index", "ndvi").RequiredFixtureIds
            .Should().Equal("spectral-index.ndvi-zero-denominator.v1");
        Find("raster.spectral-index", "ndwi").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.spectral-index", "savi").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.statistics", "population").RequiredFixtureIds
            .Should().Equal("statistics.nodata-population.v1");
        Find("raster.statistics", "default").RequiredFixtureIds.Should().BeEmpty();
        Find("raster.histogram", "equal-width").RequiredFixtureIds
            .Should().Equal("histogram.bin-boundaries.v1");
        Find("surface.rugosity-tri", "three-by-three").RequiredFixtureIds
            .Should().Equal("surface.rugosity-tri-three-by-three.v1");
    }

    [UnitTest]
    public void Discover_NoDurableExecutorsOrProviderProofs_LeavesEveryRowUnavailable()
    {
        var discoveries = PostgisRasterOperationCapabilityMatrix.Discover(
            Runtime(),
            executors: [],
            proofs: []);

        discoveries.Should().HaveSameCount(PostgisRasterOperationCapabilityMatrix.Rows);
        discoveries.Should().OnlyContain(discovery =>
            discovery.Capability.Availability == RasterProviderAvailability.Unavailable
            && !discovery.HasDurableReferenceOutputExecutor
            && !discovery.HasProviderProof);
        discoveries.Should().OnlyContain(discovery => discovery.Rejections.Any(rejection =>
            rejection.Code == RasterProviderCapabilityRejectionCodes.DurableReferenceExecutorMissing));
    }

    [UnitTest]
    public void ProjectOperations_CurrentMatrixIsAcceptedByRast010RegistryButNeverAdvertised()
    {
        var discoveries = PostgisRasterOperationCapabilityMatrix.Discover(
            Runtime(),
            executors: [],
            proofs: []);
        var projected = RasterProviderCapabilityMatrix.ProjectOperations(discoveries);

        var registry = RasterEngineCapabilityRegistry.CreateForProviderCapabilities(
            projected,
            RasterEngineCapabilityRegistry.DefaultGdalRasterInputFormatNames,
            RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames);

        foreach (var processId in projected.Select(capability => capability.Variant.ProcessId))
        {
            registry.Find(processId)!.Engines.Single(engine => engine.Engine == RasterEngine.Postgis)
                .IsAvailable.Should().BeFalse(processId);
        }
    }

    private static RasterProviderOperationCapabilityRow Find(string processId, string variant) =>
        PostgisRasterOperationCapabilityMatrix.Rows.Single(row =>
            row.ProcessId == processId && row.SemanticVariantId == variant);

    private static RasterProviderRuntimeSnapshot Runtime() => new()
    {
        ProviderId = "postgis",
        Engine = RasterEngine.Postgis,
        RuntimeVersion = "3.4.0",
        Extensions =
        [
            new RasterProviderExtensionSnapshot
            {
                ExtensionName = "postgis_raster",
                Version = "3.4.0",
            },
        ],
    };
}
