// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Testing.Tests;

public sealed class RasterEngineCapabilityRegistrationTests
{
    [UnitTest]
    public void AddGeoprocessing_ConfiguredGdalInputFormats_ProjectsEffectiveCatalogMetadata()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GdalWorker:AllowedRasterInputFormats:0"] = "TIFF",
                ["GdalWorker:AllowedRasterInputFormats:1"] = "JPEG2000",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeoprocessing(configuration);

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IProcessCatalog>();
        var conversion = catalog.GetProcess("conversion.raster-format");
        var gdal = conversion!.RasterEngineCapabilities!.Engines
            .Single(engine => engine.Engine == RasterEngine.GdalNative);

        gdal.Formats.InputMediaTypes.Should().Equal("image/tiff", "image/jp2");
    }
}
