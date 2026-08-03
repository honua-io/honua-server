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
            Assert.NotEmpty(capability.SemanticVariants);
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
                Assert.NotEmpty(engine.TestedRuntimeVersion);
                Assert.All(engine.VerifiedSemanticVariants, variant =>
                    Assert.Contains(variant, capability.SemanticVariants));
            });
        });
    }

    [Fact]
    public void BuiltInCapabilities_ClipAdvertisesRasterAndBoundaryInputFormats()
    {
        var capability = new RasterEngineCapabilityRegistry().Find("raster.clip");

        Assert.NotNull(capability);
        var postgis = capability.Engines.Single(engine => engine.Engine == RasterEngine.Postgis);
        var gdal = capability.Engines.Single(engine => engine.Engine == RasterEngine.GdalNative);
        Assert.Equal(new[] { "image/tiff", "application/wkb" }, postgis.Formats.InputMediaTypes);
        Assert.Equal(
            new[] { "image/tiff", "image/png", "image/jpeg", "application/wkb" },
            gdal.Formats.InputMediaTypes);
    }

    [Fact]
    public void BuiltInCapabilities_RasterFormatConversionAdvertisesDefaultInputs()
    {
        var capability = new RasterEngineCapabilityRegistry().Find("conversion.raster-format");

        Assert.NotNull(capability);
        Assert.All(capability.Engines, engine =>
            Assert.Equal(
                new[] { "image/tiff", "image/png", "image/jpeg" },
                engine.Formats.InputMediaTypes));
    }

    [Fact]
    public void ConfiguredGdalFormats_ProjectAcrossRasterExecutorsAndPreserveAuxiliaryInputs()
    {
        var registry = RasterEngineCapabilityRegistry.CreateForGdalRasterInputFormats(
            ["JPEG2000"],
            ["VRT", "WMS"]);

        Assert.Equal(
            new[] { "image/jp2" },
            GdalInputs(registry, "surface.slope"));
        Assert.Equal(
            new[] { "image/jp2", "application/wkb" },
            GdalInputs(registry, "raster.clip"));
        Assert.Equal(
            new[] { "image/jp2", "application/geo+json" },
            GdalInputs(registry, "raster.zonal-statistics"));
        Assert.Equal(
            new[] { "application/geo+json" },
            GdalInputs(registry, "conversion.rasterize"));
    }

    [Fact]
    public void ConfiguredGdalFormatWithEveryBackingDriverSkipped_IsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RasterEngineCapabilityRegistry.CreateForGdalRasterInputFormats(
                ["JPEG2000"],
                RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames));

        Assert.Contains("JPEG2000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SkipDrivers", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PNG", "GTiff", "surface.slope")]
    [InlineData("TIFF", "COG", "conversion.raster-format")]
    [InlineData("TIFF", "PNG", "conversion.raster-format")]
    [InlineData("TIFF", "JPEG", "conversion.raster-format")]
    [InlineData("TIFF", "GeoJSON", "conversion.polygonize")]
    public void ConfiguredGdalRequiredOutputDriverSkipped_IsRejected(
        string allowedInputFormat,
        string skippedDriver,
        string affectedProcess)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RasterEngineCapabilityRegistry.CreateForGdalRasterInputFormats(
                [allowedInputFormat],
                [skippedDriver]));

        Assert.Contains(skippedDriver, exception.Message, StringComparison.Ordinal);
        Assert.Contains(affectedProcess, exception.Message, StringComparison.Ordinal);
        Assert.Contains("output driver", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be advertised", exception.Message, StringComparison.Ordinal);
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
        Assert.Equal(capability.SemanticVariants, roundTrip.SemanticVariants);
        Assert.Equal(capability.Engines.Count, roundTrip.Engines.Count);
        Assert.Equal(json, RasterEngineCapabilityJson.Serialize(roundTrip));
        Assert.Contains("\"gdalNative\"", json, StringComparison.Ordinal);
        Assert.Contains("\"postgis\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInCapabilities_BlockUnverifiedPostgisDynamicRouting()
    {
        var registry = new RasterEngineCapabilityRegistry();

        Assert.All(registry.Processes, process =>
        {
            var postgis = process.Engines.Single(engine => engine.Engine == RasterEngine.Postgis);
            Assert.Equal(RasterSemanticConformanceStatus.Unverified, postgis.SemanticConformance);
            Assert.Empty(postgis.VerifiedSemanticVariants);
            Assert.All(process.SemanticVariants, variant => Assert.False(postgis.SupportsSemanticVariant(variant)));
        });
    }

    [Fact]
    public void BuiltInCapabilities_PinGdalBaselineAndLinkKnownFixtureEvidence()
    {
        var capability = new RasterEngineCapabilityRegistry().Find("raster.resample");

        Assert.NotNull(capability);
        var gdal = capability.Engines.Single(engine => engine.Engine == RasterEngine.GdalNative);
        Assert.Equal(RasterSemanticConformanceStatus.CanonicalBaseline, gdal.SemanticConformance);
        Assert.Equal("3.13.1", gdal.TestedRuntimeVersion);
        Assert.Contains("bilinear", gdal.VerifiedSemanticVariants);
        Assert.Contains("resample.bilinear-nodata-edge.v1", gdal.SemanticEvidenceFixtureIds);
        Assert.True(gdal.SupportsSemanticVariant("bilinear"));
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
    [InlineData("semanticConformance")]
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
            "semanticConformance" => engine with
            {
                SemanticConformance = (RasterSemanticConformanceStatus)100,
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
    public void Constructor_UnverifiedEngineWithSemanticEvidence_IsRejected()
    {
        var template = new RasterEngineCapabilityRegistry().Find("raster.resample")!;
        var malformed = template with
        {
            Engines = template.Engines
                .Select(engine => engine.Engine == RasterEngine.GdalNative
                    ? engine with { SemanticConformance = RasterSemanticConformanceStatus.Unverified }
                    : engine)
                .ToArray(),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new RasterEngineCapabilityRegistry([malformed]));

        Assert.Equal("capabilities", exception.ParamName);
        Assert.Contains("cannot advertise semantic evidence", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requiredCapabilities", null)]
    [InlineData("requiredCapabilities", " ")]
    [InlineData("inputMediaTypes", null)]
    [InlineData("inputMediaTypes", "")]
    [InlineData("outputMediaTypes", null)]
    [InlineData("outputMediaTypes", "\t")]
    public void Constructor_NullOrBlankStringMetadataEntries_AreRejected(
        string field,
        string? value)
    {
        var template = new RasterEngineCapabilityRegistry().Processes[0];
        var engine = template.Engines[0];
        var malformedEngine = field switch
        {
            "requiredCapabilities" => engine with { RequiredCapabilities = [value!] },
            "inputMediaTypes" => engine with
            {
                Formats = engine.Formats with { InputMediaTypes = [value!] },
            },
            "outputMediaTypes" => engine with
            {
                Formats = engine.Formats with { OutputMediaTypes = [value!] },
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
        Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_SnapshotsTheFullDescriptorGraph()
    {
        var template = new RasterEngineCapabilityRegistry().Find("raster.resample")!;
        var semanticVariants = template.SemanticVariants.ToArray();
        var requiredCapabilities = template.Engines[1].RequiredCapabilities.ToArray();
        var inputMediaTypes = template.Engines[1].Formats.InputMediaTypes.ToArray();
        var outputMediaTypes = template.Engines[1].Formats.OutputMediaTypes.ToArray();
        var inputResidencies = template.Engines[1].InputResidencies.ToArray();
        var outputSinks = template.Engines[1].OutputSinks.ToArray();
        var verifiedVariants = template.Engines[1].VerifiedSemanticVariants.ToArray();
        var evidenceFixtureIds = template.Engines[1].SemanticEvidenceFixtureIds.ToArray();
        var knownDivergences = new[] { "documented.test-divergence" };
        var engines = template.Engines.ToArray();
        engines[1] = engines[1] with
        {
            RequiredCapabilities = requiredCapabilities,
            Formats = engines[1].Formats with
            {
                InputMediaTypes = inputMediaTypes,
                OutputMediaTypes = outputMediaTypes,
            },
            InputResidencies = inputResidencies,
            OutputSinks = outputSinks,
            VerifiedSemanticVariants = verifiedVariants,
            SemanticEvidenceFixtureIds = evidenceFixtureIds,
            KnownSemanticDivergences = knownDivergences,
        };
        var source = new[] { template with { SemanticVariants = semanticVariants, Engines = engines } };
        var registry = new RasterEngineCapabilityRegistry(source);

        source[0] = source[0] with { ProcessId = "mutated.process" };
        engines[1] = engines[1] with { ImplementationVersion = "mutated" };
        semanticVariants[0] = "mutated.variant";
        requiredCapabilities[0] = "mutated.capability";
        inputMediaTypes[0] = "application/mutated-input";
        outputMediaTypes[0] = "application/mutated-output";
        inputResidencies[0] = RasterInputResidency.Inline;
        outputSinks[0] = RasterOutputSink.ObjectStore;
        verifiedVariants[0] = "mutated.verified-variant";
        evidenceFixtureIds[0] = "mutated.fixture";
        knownDivergences[0] = "mutated.divergence";

        var snapshot = Assert.Single(registry.Processes);
        Assert.Equal(template.ProcessId, snapshot.ProcessId);
        Assert.Equal(template.SemanticVariants, snapshot.SemanticVariants);
        Assert.Equal(template.Engines[1].ImplementationVersion, snapshot.Engines[1].ImplementationVersion);
        Assert.Equal(template.Engines[1].RequiredCapabilities, snapshot.Engines[1].RequiredCapabilities);
        Assert.Equal(template.Engines[1].Formats.InputMediaTypes, snapshot.Engines[1].Formats.InputMediaTypes);
        Assert.Equal(template.Engines[1].Formats.OutputMediaTypes, snapshot.Engines[1].Formats.OutputMediaTypes);
        Assert.Equal(template.Engines[1].InputResidencies, snapshot.Engines[1].InputResidencies);
        Assert.Equal(template.Engines[1].OutputSinks, snapshot.Engines[1].OutputSinks);
        Assert.Equal(template.Engines[1].VerifiedSemanticVariants, snapshot.Engines[1].VerifiedSemanticVariants);
        Assert.Equal(template.Engines[1].SemanticEvidenceFixtureIds, snapshot.Engines[1].SemanticEvidenceFixtureIds);
        Assert.Equal("documented.test-divergence", Assert.Single(snapshot.Engines[1].KnownSemanticDivergences));
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

    private static IReadOnlyList<string> GdalInputs(
        RasterEngineCapabilityRegistry registry,
        string processId) => registry
            .Find(processId)!
            .Engines
            .Single(engine => engine.Engine == RasterEngine.GdalNative)
            .Formats
            .InputMediaTypes;
}
