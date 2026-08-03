// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class PostgisSurfaceZonalExecutionContractTests
{
    [UnitTest]
    public void ParameterKeys_BuildCanonicalDurableKeys()
    {
        RasterProcessExecutionParameterKeys.StepInput(0, "zFactor")
            .Should().Be("honua.geoprocessing.step.0.zFactor");
        RasterProcessExecutionParameterKeys.StepRasterSource(2, "source")
            .Should().Be("honua.geoprocessing.raster_source.2.source");
    }

    [UnitTest]
    public void Bind_SlopeDefaults_ReturnsExactTypedSourceAndCanonicalSemantics()
    {
        var source = Source();

        var result = PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.SlopeProcessId,
            "tenant-a",
            Parameters(source));

        result.Should().BeOfType<PostgisSlopeBinding>().Which.Should().BeEquivalentTo(
            new PostgisSlopeBinding
            {
                ProcessId = "surface.slope",
                Source = source,
                Units = SlopeUnits.Degrees,
                ZFactor = 1d,
            });
    }

    [UnitTest]
    public void Bind_Hillshade_UsesInvariantValidatedParameters()
    {
        var parameters = Parameters(Source());
        Input(parameters, "azimuth", "270.5");
        Input(parameters, "altitude", "30.25");
        Input(parameters, "zFactor", "2.5");

        var result = PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.HillshadeProcessId,
            "tenant-a",
            parameters);

        result.Should().BeOfType<PostgisHillshadeBinding>().Which.Should().BeEquivalentTo(
            new
            {
                AzimuthDegrees = 270.5,
                AltitudeDegrees = 30.25,
                ZFactor = 2.5,
            });
    }

    [UnitTest]
    public void Bind_ZonalStatistics_CanonicalizesAndDeduplicatesStatistics()
    {
        var parameters = Parameters(Source());
        Input(parameters, "zonesLayerId", "17");
        Input(parameters, "band", "2");
        Input(parameters, "statistics", "MEAN,count,mean,variance");

        var result = PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.ZonalStatisticsProcessId,
            "tenant-a",
            parameters);

        var zonal = result.Should().BeOfType<PostgisZonalStatisticsBinding>().Which;
        zonal.ZonesLayerId.Should().Be(17);
        zonal.Band.Should().Be(2);
        zonal.Statistics.Should().Equal("mean", "count", "variance");
    }

    [UnitTest]
    public void Bind_ZonalStatistics_InlineZones_FailsEngineVariantFence()
    {
        var parameters = Parameters(Source());
        Input(parameters, "zonesLayerId", "17");
        Input(parameters, "zones", "base64-zone-payload");

        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.ZonalStatisticsProcessId,
            "tenant-a",
            parameters);

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.UnsupportedInputVariant);
    }

    [UnitTest]
    public void Bind_ZonalStatistics_MissingZonesLayer_FailsBeforeProviderIo()
    {
        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.ZonalStatisticsProcessId,
            "tenant-a",
            Parameters(Source()));

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.MissingParameter);
    }

    [UnitTest]
    public void Bind_ObjectStoreSource_FailsPostgisResidencyFence()
    {
        var source = new ObjectStoreCogRasterSourceDescriptor
        {
            StoreReference = "store-a",
            ObjectKey = "dem/source.tif",
            Version = "object-v1",
            Content = Content(),
            SecurityContext = SecurityContext(),
        };

        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.AspectProcessId,
            "tenant-a",
            Parameters(source));

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.UnsupportedSourceResidency);
    }

    [UnitTest]
    public void Bind_SourceTenantMismatch_FailsConsistencyFence()
    {
        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.AspectProcessId,
            "tenant-b",
            Parameters(Source()));

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.TenantMismatch);
    }

    [UnitTest]
    public void Bind_TypedAndLegacySource_FailsAmbiguityFence()
    {
        var parameters = Parameters(Source());
        Input(parameters, "rasterId", "42");

        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.AspectProcessId,
            "tenant-a",
            parameters);

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.AmbiguousSource);
    }

    [UnitTest]
    public void Bind_SourceSelection_FailsInsteadOfSilentlyIgnoringIt()
    {
        var source = Source() with
        {
            Selection = new RasterSourceSelection
            {
                Bands = [1],
            },
        };

        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.AspectProcessId,
            "tenant-a",
            Parameters(source));

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.UnsupportedSelection);
    }

    [UnitTest]
    public void Bind_SlopeRadians_FailsUnprovedSemanticVariant()
    {
        var parameters = Parameters(Source());
        Input(parameters, "units", "radians");

        var act = () => PostgisSurfaceZonalExecutionContract.Bind(
            PostgisSurfaceZonalExecutionContract.SlopeProcessId,
            "tenant-a",
            parameters);

        act.Should().Throw<PostgisSurfaceZonalBindingException>()
            .Which.Code.Should().Be(PostgisSurfaceZonalBindingCodes.InvalidParameter);
    }

    private static Dictionary<string, string> Parameters(RasterSourceDescriptor source) => new()
    {
        [RasterProcessExecutionParameterKeys.StepRasterSource(0, "source")] =
            RasterSourceJson.Serialize(source),
    };

    private static void Input(Dictionary<string, string> parameters, string name, string value) =>
        parameters[RasterProcessExecutionParameterKeys.StepInput(0, name)] = value;

    private static PostgisRasterSourceDescriptor Source() => new()
    {
        LayerId = 7,
        RasterId = 42,
        Version = "catalog-v1",
        Content = Content(),
        SecurityContext = SecurityContext(),
    };

    private static RasterContentIdentity Content() => new()
    {
        SizeBytes = 4096,
        MediaType = "image/tiff",
    };

    private static RasterSecurityContextReference SecurityContext() => new()
    {
        TenantId = "tenant-a",
        AuthorizationSnapshotReference = "auth-snapshot-1",
    };
}
