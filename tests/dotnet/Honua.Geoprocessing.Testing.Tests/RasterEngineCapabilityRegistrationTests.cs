// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Testing.Tests;

public sealed class RasterEngineCapabilityRegistrationTests
{
    [UnitTest]
    public void AddGeoprocessing_DefaultWebComposition_DoesNotRegisterRasterPostgisClaimer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeoprocessing(new ConfigurationBuilder().Build());

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IJobExecutor)
            && descriptor.ImplementationType == typeof(RasterPostgisDispatchJobExecutor));
    }

    [UnitTest]
    public void AddRasterPostgisExecutionDispatcher_DedicatedCompositionAcceptsOnlyRasterPostgis()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRasterProviderExecutor<FakePostgisExecutor>();
        services.AddRasterPostgisExecutionDispatcher();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetServices<IJobExecutor>().Should().ContainSingle().Subject;

        dispatcher.AcceptedRuntimeProfiles.Should().Equal(RuntimeProfiles.RasterPostgis);
        RuntimeProfiles.CanClaim(dispatcher.AcceptedRuntimeProfiles, RuntimeProfiles.Managed).Should().BeFalse();
        RuntimeProfiles.CanClaim(dispatcher.AcceptedRuntimeProfiles, RuntimeProfiles.Native).Should().BeFalse();
        RuntimeProfiles.CanClaim(dispatcher.AcceptedRuntimeProfiles, RuntimeProfiles.CustomCode).Should().BeFalse();
    }

    [UnitTest]
    public void AddGeoprocessing_DiscoveredPostgisExecutor_ProjectsAvailableCapability()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRasterProviderExecutor<FakePostgisExecutor>();
        services.AddGeoprocessing(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IRasterEngineCapabilityRegistry>();
        var postgis = registry.Find("raster.clip")!.Engines
            .Single(engine => engine.Engine == RasterEngine.Postgis);

        postgis.IsAvailable.Should().BeTrue();
        postgis.ProviderId.Should().Be("postgis");
        postgis.ProviderPolicyVersion.Should().Be("postgis-raster-v1");
    }

    [UnitTest]
    public void AddGeoprocessing_ConfiguredGdalInputFormats_ProjectsEffectiveCatalogMetadata()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GdalWorker:AllowedRasterInputFormats:0"] = "TIFF",
                ["GdalWorker:AllowedRasterInputFormats:1"] = "JPEG2000",
                ["GdalWorker:Hardening:SkipDrivers:0"] = "VRT",
                ["GdalWorker:Hardening:SkipDrivers:1"] = "WMS",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeoprocessing(configuration);

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IProcessCatalog>();
        var conversion = catalog.GetProcess("conversion.raster-format");
        var surface = catalog.GetProcess("surface.slope");
        var conversionGdal = conversion!.RasterEngineCapabilities!.Engines
            .Single(engine => engine.Engine == RasterEngine.GdalNative);
        var surfaceGdal = surface!.RasterEngineCapabilities!.Engines
            .Single(engine => engine.Engine == RasterEngine.GdalNative);

        conversionGdal.Formats.InputMediaTypes.Should().Equal("image/tiff", "image/jp2");
        surfaceGdal.Formats.InputMediaTypes.Should().Equal("image/tiff", "image/jp2");
    }

    private sealed class FakePostgisExecutor : IRasterProviderExecutor
    {
        public IReadOnlyList<RasterProviderCapability> Capabilities { get; } =
        [
            new RasterProviderCapability
            {
                ProviderId = "postgis",
                Engine = RasterEngine.Postgis,
                Variant = new RasterSemanticVariant
                {
                    ProcessId = "raster.clip",
                    SemanticVersion = "1.0.0",
                    ImplementationVersion = "honua.postgis.raster.clip@1.0.0",
                },
                PolicyVersion = "postgis-raster-v1",
                Availability = RasterProviderAvailability.Available,
            },
        ];

        public Task<RasterProviderExecutionResult> ExecuteAsync(
            RasterProviderExecutionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
