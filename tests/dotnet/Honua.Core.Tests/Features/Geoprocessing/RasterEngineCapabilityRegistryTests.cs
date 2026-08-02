// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterEngineCapabilityRegistryTests
{
    [Fact]
    public void BuiltInCapabilities_AreProviderNeutralAndWellFormed()
    {
        var registry = new RasterEngineCapabilityRegistry();

        Assert.NotEmpty(registry.Processes);
        Assert.Equal(
            registry.Processes.Count,
            registry.Processes.Select(capability => capability.ProcessId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(registry.Processes, capability =>
        {
            Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", capability.SemanticVersion);
            Assert.Equal(
                new[] { RasterEngine.Postgis, RasterEngine.GdalNative },
                capability.Engines.Select(engine => engine.Engine).OrderBy(engine => engine).ToArray());
            Assert.All(capability.Engines, engine =>
            {
                Assert.NotEmpty(engine.ImplementationVersion);
                Assert.NotEmpty(engine.RequiredCapabilities);
                Assert.NotEmpty(engine.InputResidencies);
                Assert.NotEmpty(engine.OutputSinks);
                Assert.True(engine.Formats.InputMediaTypes.Count > 0);
                Assert.True(engine.Formats.OutputMediaTypes.Count > 0);
                Assert.Equal(engine.IsAvailable, engine.UnavailabilityReason is null);
            });
        });
    }

    [Fact]
    public void Estimate_UnknownMetadata_SaturatesAndRefusesRequestExecution()
    {
        var registry = new RasterEngineCapabilityRegistry();

        var estimate = registry.Estimate(
            "surface.slope",
            RasterEngine.GdalNative,
            new RasterCostEstimatorInput());

        Assert.True(estimate.UsesConservativeValues);
        Assert.Equal(long.MaxValue, estimate.InputPixels);
        Assert.Equal(long.MaxValue, estimate.DecodedBytes);
        Assert.Contains("inputPixels", estimate.UnknownInputs);
        Assert.Contains("decodedBytes", estimate.UnknownInputs);
        Assert.False(estimate.RequestExecutionAllowed);
        Assert.Contains("metadata", estimate.RequestExecutionUnavailabilityReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_KnownMetadata_PreservesEveryEstimatorInput()
    {
        var registry = new RasterEngineCapabilityRegistry();
        var input = new RasterCostEstimatorInput
        {
            SourceCount = 2,
            BandCount = 4,
            ZoneCount = 12,
            InputPixels = 1_000,
            OutputPixels = 2_000,
            DecodedBytes = 32_000,
            ExpectedScratchBytes = 64_000,
            ExpectedDatabaseWork = 3_000,
        };

        var estimate = registry.Estimate("raster.zonal-statistics", RasterEngine.GdalNative, input);

        Assert.False(estimate.UsesConservativeValues);
        Assert.Empty(estimate.UnknownInputs);
        Assert.Equal(input.SourceCount, estimate.SourceCount);
        Assert.Equal(input.BandCount, estimate.BandCount);
        Assert.Equal(input.ZoneCount, estimate.ZoneCount);
        Assert.Equal(input.InputPixels, estimate.InputPixels);
        Assert.Equal(input.OutputPixels, estimate.OutputPixels);
        Assert.Equal(input.DecodedBytes, estimate.DecodedBytes);
        Assert.Equal(input.ExpectedScratchBytes, estimate.ExpectedScratchBytes);
        Assert.Equal(input.ExpectedDatabaseWork, estimate.ExpectedDatabaseWork);
        Assert.False(estimate.RequestExecutionAllowed, "native GDAL never runs in the request-serving process");
    }

    [Fact]
    public void Capability_SourceGeneratedJson_RoundTrips()
    {
        var registry = new RasterEngineCapabilityRegistry();
        var capability = registry.Find("raster.reproject");

        Assert.NotNull(capability);
        var json = RasterEngineCapabilityJson.Serialize(capability!);
        var roundTrip = RasterEngineCapabilityJson.Deserialize(json);

        Assert.Equal(capability.ProcessId, roundTrip.ProcessId);
        Assert.Equal(capability.SemanticVersion, roundTrip.SemanticVersion);
        Assert.Equal(capability.Engines.Count, roundTrip.Engines.Count);
        Assert.Equal(json, RasterEngineCapabilityJson.Serialize(roundTrip));
        Assert.Contains("\"gdalNative\"", json, StringComparison.Ordinal);
        Assert.Contains("\"postgis\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Estimate_NegativeMetric_IsRejected()
    {
        var registry = new RasterEngineCapabilityRegistry();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => registry.Estimate(
            "surface.slope",
            RasterEngine.GdalNative,
            new RasterCostEstimatorInput { SourceCount = -1 }));

        Assert.Equal("input", exception.ParamName);
    }
}
