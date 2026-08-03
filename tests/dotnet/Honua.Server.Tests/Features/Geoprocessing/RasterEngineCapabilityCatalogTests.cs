// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Protocol(TestProtocols.Grpc)]
public sealed class RasterEngineCapabilityCatalogTests
{
    private readonly RasterEngineCapabilityRegistry _registry = new();
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_EveryRasterEngineCandidate_HasCapabilityMetadata()
    {
        var expected = _catalog.ListProcesses()
            .Where(IsRasterEngineCandidate)
            .Select(process => process.ProcessId)
            .OrderBy(processId => processId, StringComparer.Ordinal)
            .ToArray();
        var registered = _registry.Processes
            .Select(capability => capability.ProcessId)
            .OrderBy(processId => processId, StringComparer.Ordinal)
            .ToArray();

        expected.Should().HaveCount(27);
        registered.Should().Equal(expected);
        foreach (var processId in expected)
        {
            var definition = _catalog.GetProcess(processId)!;
            definition.RasterEngineCapabilities.Should().NotBeNull();
            definition.RasterEngineCapabilities!.Engines.Should().HaveCount(2);
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_NonRasterConversions_KeepRuntimeProfilesAndDoNotAdvertiseRasterEngines()
    {
        var expectedProfiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["conversion.geometry-format"] = RuntimeProfiles.Managed,
            ["conversion.feature-project"] = RuntimeProfiles.Managed,
            ["gdal.ogr2ogr"] = RuntimeProfiles.Native,
            ["pcloud.translate"] = RuntimeProfiles.Native,
        };

        foreach (var (processId, expectedProfile) in expectedProfiles)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull();
            definition!.RuntimeProfile.Should().Be(expectedProfile);
            definition.RasterEngineCapabilities.Should().BeNull();
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_PostgisOptionsAreHonestUntilCanonicalExecutorsLand()
    {
        _registry.Processes.Should().AllSatisfy(process =>
        {
            var postgis = process.Engines.Single(engine => engine.Engine == RasterEngine.Postgis);
            postgis.IsAvailable.Should().BeFalse();
            postgis.UnavailabilityReason.Should().NotBeNullOrWhiteSpace();
        });

        var slope = _registry.Find("surface.slope")!;
        slope.Engines.Single(engine => engine.Engine == RasterEngine.Postgis)
            .DefaultPreference.Should().Be(RasterEngineDefaultPreference.Preferred);
        slope.Engines.Single(engine => engine.Engine == RasterEngine.GdalNative)
            .DefaultPreference.Should().Be(RasterEngineDefaultPreference.Fallback);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_KrigingAdvertisesNoExecutableEngine()
    {
        var capability = _registry.Find("raster.interpolate-kriging");

        capability.Should().NotBeNull();
        capability!.Engines.Should().OnlyContain(engine => !engine.IsAvailable);
        capability.Engines.Should().AllSatisfy(engine =>
            engine.UnavailabilityReason.Should().NotBeNullOrWhiteSpace());
    }

    private static bool IsRasterEngineCandidate(ProcessDefinition process)
        => process.Category is "surface" or "raster"
            || (process.Category == "conversion"
                && (process.OutputArtifactKinds.Contains(ArtifactKind.Raster)
                    || process.Parameters.Any(parameter => parameter.AcceptsRasterSource)))
            || process.ProcessId is "proximity.euclidean-distance" or "proximity.euclidean-allocation";
}
