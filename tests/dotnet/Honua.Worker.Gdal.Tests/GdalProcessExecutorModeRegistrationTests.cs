// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
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
