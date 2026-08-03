// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Verifies that the worker registration REPLACES (rather than appends onto) the
/// pre-populated <see cref="GdalWorkerOptions.AllowedRasterInputFormats"/> and
/// <see cref="GdalHardeningOptions.SkipDrivers"/> lists when the operator configures
/// them. ConfigurationBinder appends array items onto a non-empty default list, so
/// without the post-bind replacement an operator could never TIGHTEN the raster-format
/// allowlist nor REMOVE a default skip driver — exactly the workflow the XML docs
/// promise (#2784 follow-up).
/// </summary>
public sealed class GdalWorkerOptionsConfigBindingTests
{
    [UnitTest]
    public void AddGdalProcessExecutors_AllowedRasterInputFormatsConfigured_ReplacesDefaults()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GdalWorker:AllowedRasterInputFormats:0"] = "TIFF",
        });

        var options = provider.GetRequiredService<IOptions<GdalWorkerOptions>>().Value;

        // Config supplies TIFF only → effective allowlist is exactly TIFF (defaults
        // PNG/JPEG dropped, not appended-to).
        options.AllowedRasterInputFormats.Should().ContainSingle().Which.Should().Be("TIFF");

        // And a PNG payload is now refused because PNG left the allowlist.
        GdalRasterDimensionGuard.TryAdmit(TiffHeaderBuilder.Png(16, 16), options, out var error)
            .Should().BeFalse();
        error.Should().Contain("PNG").And.Contain("allowlist");
    }

    [UnitTest]
    public void AddGdalProcessExecutors_SkipDriversConfiguredWithoutJp2_RemovesDefaultDenial()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            // A deliberately narrower denial set that OMITS JP2OpenJPEG (the operator is
            // opting to open JPEG 2000). Without replacement semantics Bind would append,
            // leaving JP2OpenJPEG denied anyway.
            ["GdalWorker:Hardening:SkipDrivers:0"] = "VRT",
            ["GdalWorker:Hardening:SkipDrivers:1"] = "WMS",
        });

        var options = provider.GetRequiredService<IOptions<GdalHardeningOptions>>().Value;

        options.SkipDrivers.Should().BeEquivalentTo(new[] { "VRT", "WMS" });
        options.SkipDrivers.Should().NotContain("JP2OpenJPEG");

        var env = GdalRuntimeHardening.BuildEnvironment(options, inputReferencesRemoteVsi: false);
        env["GDAL_SKIP"].Should().NotContain("JP2OpenJPEG");
        env["GDAL_SKIP"].Should().Contain("VRT");
    }

    [UnitTest]
    public void AddGdalProcessExecutors_NoConfig_KeepsListDefaultsIntact()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var worker = provider.GetRequiredService<IOptions<GdalWorkerOptions>>().Value;
        var hardening = provider.GetRequiredService<IOptions<GdalHardeningOptions>>().Value;

        worker.AllowedRasterInputFormats.Should().BeEquivalentTo(new[] { "TIFF", "PNG", "JPEG" });
        hardening.SkipDrivers.Should().Equal(
            RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames);
        hardening.SkipDrivers.Should().Contain("JP2OpenJPEG").And.Contain("VRT").And.Contain("NITF");
    }

    private static ServiceProvider BuildProvider(IDictionary<string, string?> configValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddGdalProcessExecutors(configuration);

        return services.BuildServiceProvider();
    }
}
