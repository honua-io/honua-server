// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Verifies the mode gating of the native GDAL executor registration seam
/// (<see cref="GdalWorkerServiceCollectionExtensions.AddGdalProcessExecutors(IServiceCollection, IConfiguration, GdalProcessExecutorMode)"/>,
/// issue #2180): the default / <see cref="GdalProcessExecutorMode.InProcess"/> mode
/// keeps the fast host-CLI runner (no regression to the sub-second managed loop),
/// while <see cref="GdalProcessExecutorMode.Container"/> swaps in the container-exec
/// runner — registering the IDENTICAL executor set either way, so only the
/// <see cref="IGdalCommandRunner"/> implementation differs.
/// </summary>
public sealed class GdalProcessExecutorModeRegistrationTests
{
    [UnitTest]
    public void AddGdalProcessExecutors_DefaultMode_RegistersInProcessRunner()
    {
        using var provider = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config));

        provider.GetRequiredService<IGdalCommandRunner>().Should().BeOfType<ProcessGdalCommandRunner>();
    }

    [UnitTest]
    public void AddGdalProcessExecutors_InProcessMode_RegistersInProcessRunner()
    {
        using var provider = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config, GdalProcessExecutorMode.InProcess));

        provider.GetRequiredService<IGdalCommandRunner>().Should().BeOfType<ProcessGdalCommandRunner>();
    }

    [UnitTest]
    public void AddGdalProcessExecutors_ContainerMode_RegistersDockerRunner_AndInvoker()
    {
        using var provider = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config, GdalProcessExecutorMode.Container));

        provider.GetRequiredService<IGdalCommandRunner>().Should().BeOfType<DockerGdalCommandRunner>();
        provider.GetRequiredService<IDockerCommandInvoker>().Should().BeOfType<ProcessDockerCommandInvoker>();
    }

    [UnitTest]
    public void AddGdalProcessExecutors_BothModes_RegisterIdenticalExecutorSet()
    {
        using var inProcess = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config, GdalProcessExecutorMode.InProcess));
        using var container = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config, GdalProcessExecutorMode.Container));

        // The executor set is the same regardless of how GDAL is reached.
        var inProcessExecutorTypes = ExecutorTypeNames(inProcess);
        var containerExecutorTypes = ExecutorTypeNames(container);

        containerExecutorTypes.Should().BeEquivalentTo(inProcessExecutorTypes);
        containerExecutorTypes.Should().NotBeEmpty();
    }

    [UnitTest]
    public void AddGdalProcessExecutors_ContainerMode_BindsImageFromConfiguration()
    {
        const string customImage = "ghcr.io/honua-io/honua-worker-etl:pinned";
        using var provider = BuildProvider(
            static (services, config) => services.AddGdalProcessExecutors(config, GdalProcessExecutorMode.Container),
            new Dictionary<string, string?> { ["GdalContainer:Image"] = customImage });

        var options = provider
            .GetRequiredService<IOptions<GdalContainerExecutionOptions>>()
            .Value;

        options.Image.Should().Be(customImage);
    }

    [UnitTest]
    public void AddGdalProcessExecutors_RegistersExactlyTheExpectedNativeProcessIdSet()
    {
        using var provider = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config));

        var registeredIds = provider
            .GetServices<IProcessExecutor>()
            .SelectMany(e => e.ProcessIds)
            .ToHashSet(StringComparer.Ordinal);

        // The full process-id surface the GDAL worker exposes. Asserting the exact set
        // (not just mode-to-mode equality) makes adding or dropping an executor a
        // deliberate, reviewed change rather than a silent drift.
        registeredIds.Should().BeEquivalentTo(new[]
        {
            // Vector / raster passthrough + canonicalization.
            "gdal.ogr2ogr",
            "gdal.gdalwarp",
            "transform.reproject",
            "source.ogr",
            // Raster analysis tool pack (#2141).
            "raster.resample",
            "raster.interpolate-idw",
            "raster.interpolate-kriging",
            "raster.mosaic",
            // Raster analysis & terrain GP tool packs (#2239, #2240).
            "raster.map-algebra",
            "raster.spectral-index",
            "raster.reclassify",
            "proximity.euclidean-distance",
            "proximity.euclidean-allocation",
            "surface.contour",
            "surface.viewshed",
            "conversion.polygonize",
            "conversion.rasterize",
            // Surface / terrain derivatives.
            "surface.slope",
            "surface.aspect",
            "surface.hillshade",
            "surface.rugosity-tri",
            "surface.rugosity-tpi",
            "surface.roughness",
            // Raster clip / reproject / format conversion.
            "raster.clip",
            "raster.reproject",
            "conversion.raster-reproject",
            "conversion.raster-format",
            // Raster statistics.
            "raster.statistics",
            "raster.histogram",
            "raster.zonal-statistics",
            // Coverage metadata + point-cloud translate.
            "coverage.multidim.metadata",
            "pcloud.translate",
        });
    }

    [UnitTest]
    public void AddGdalProcessExecutors_RoutesEveryNativeRoutedCatalogProcess()
    {
        // Cross-project honesty: the managed-side CatalogExecutableConformanceTests
        // proves these native-routed ids are ABSENT from the lean dispatcher; here we
        // prove the GDAL worker actually routes every native-profile catalog process,
        // so a catalog entry can never claim native execution with no worker executor.
        using var provider = BuildProvider(static (services, config) =>
            services.AddGdalProcessExecutors(config));

        var supported = provider
            .GetServices<IProcessExecutor>()
            .SelectMany(e => e.ProcessIds)
            .ToHashSet(StringComparer.Ordinal);

        var catalog = new BuiltInProcessCatalog();
        var nativeRouted = catalog.ListProcesses()
            .Where(p => RuntimeProfiles.Normalize(p.RuntimeProfile) == RuntimeProfiles.Native)
            .Select(p => p.ProcessId)
            .ToList();

        nativeRouted.Should().NotBeEmpty("the catalog advertises native-routed processes");
        foreach (var processId in nativeRouted)
        {
            supported.Should().Contain(
                processId,
                $"the catalog advertises native-routed '{processId}', so the GDAL worker dispatcher must route it");
        }
    }

    private static string[] ExecutorTypeNames(IServiceProvider provider)
        => provider
            .GetServices<IProcessExecutor>()
            .Select(e => e.GetType().FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection, IConfiguration> register,
        IDictionary<string, string?>? configValues = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        register(services, configuration);

        return services.BuildServiceProvider();
    }
}
