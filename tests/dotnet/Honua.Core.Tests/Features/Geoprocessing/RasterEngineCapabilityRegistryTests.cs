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
    public void Constructor_UndefinedEngineValues_AreRejected()
    {
        var template = new RasterEngineCapabilityRegistry().Processes[0];
        var malformed = template with
        {
            Engines = template.Engines
                .Select((engine, index) => engine with { Engine = (RasterEngine)(100 + index) })
                .ToArray(),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new RasterEngineCapabilityRegistry([malformed]));

        Assert.Equal("capabilities", exception.ParamName);
        Assert.Contains("undefined engine value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("defaultPreference")]
    [InlineData("inputResidency")]
    [InlineData("outputSink")]
    public void Constructor_UndefinedNestedEnumValues_AreRejected(string field)
    {
        var template = new RasterEngineCapabilityRegistry().Processes[0];
        var engine = template.Engines[0];
        var malformedEngine = field switch
        {
            "defaultPreference" => engine with
            {
                DefaultPreference = (RasterEngineDefaultPreference)100,
            },
            "inputResidency" => engine with
            {
                InputResidencies = [(RasterInputResidency)100],
            },
            "outputSink" => engine with
            {
                OutputSinks = [(RasterOutputSink)100],
            },
            _ => throw new InvalidOperationException($"Unknown test field '{field}'."),
        };
        var malformed = template with
        {
            Engines = [malformedEngine, template.Engines[1]],
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new RasterEngineCapabilityRegistry([malformed]));

        Assert.Equal("capabilities", exception.ParamName);
        Assert.Contains("undefined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_SnapshotsTheFullDescriptorGraph()
    {
        var template = new RasterEngineCapabilityRegistry().Processes[0];
        var requiredCapabilities = template.Engines[0].RequiredCapabilities.ToArray();
        var inputMediaTypes = template.Engines[0].Formats.InputMediaTypes.ToArray();
        var outputMediaTypes = template.Engines[0].Formats.OutputMediaTypes.ToArray();
        var inputResidencies = template.Engines[0].InputResidencies.ToArray();
        var outputSinks = template.Engines[0].OutputSinks.ToArray();
        var engines = template.Engines.ToArray();
        engines[0] = engines[0] with
        {
            RequiredCapabilities = requiredCapabilities,
            Formats = engines[0].Formats with
            {
                InputMediaTypes = inputMediaTypes,
                OutputMediaTypes = outputMediaTypes,
            },
            InputResidencies = inputResidencies,
            OutputSinks = outputSinks,
        };
        var source = new[] { template with { Engines = engines } };
        var registry = new RasterEngineCapabilityRegistry(source);

        source[0] = source[0] with { ProcessId = "mutated.process" };
        engines[0] = engines[0] with { ImplementationVersion = "mutated" };
        requiredCapabilities[0] = "mutated.capability";
        inputMediaTypes[0] = "application/mutated-input";
        outputMediaTypes[0] = "application/mutated-output";
        inputResidencies[0] = RasterInputResidency.Inline;
        outputSinks[0] = RasterOutputSink.ObjectStore;

        var snapshot = Assert.Single(registry.Processes);
        Assert.Equal(template.ProcessId, snapshot.ProcessId);
        Assert.Equal(template.Engines[0].ImplementationVersion, snapshot.Engines[0].ImplementationVersion);
        Assert.Equal(template.Engines[0].RequiredCapabilities, snapshot.Engines[0].RequiredCapabilities);
        Assert.Equal(template.Engines[0].Formats.InputMediaTypes, snapshot.Engines[0].Formats.InputMediaTypes);
        Assert.Equal(template.Engines[0].Formats.OutputMediaTypes, snapshot.Engines[0].Formats.OutputMediaTypes);
        Assert.Equal(template.Engines[0].InputResidencies, snapshot.Engines[0].InputResidencies);
        Assert.Equal(template.Engines[0].OutputSinks, snapshot.Engines[0].OutputSinks);
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
