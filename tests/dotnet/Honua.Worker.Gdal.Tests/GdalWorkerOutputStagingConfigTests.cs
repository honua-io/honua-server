// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// The worker-side staging contract (#3089): placement-agnostic static operator
/// configuration — the same configuration keys an AWS Batch worker container
/// receives as environment variables — selects the staged output store. An enabled
/// but unimplemented provider fails closed at startup instead of silently falling
/// back to inline publication, and disabled staging registers no store at all.
/// </summary>
public sealed class GdalWorkerOutputStagingConfigTests
{
    [Fact]
    public void EnvironmentStyleConfiguration_RegistersLocalStagingStore()
    {
        // Mirrors Geoprocessing__OutputStaging__* environment variables on the
        // worker container (local pool or the AWS Batch worker image).
        var root = Directory.CreateTempSubdirectory("honua-staging-config-").FullName;
        try
        {
            var provider = BuildProvider(new Dictionary<string, string?>
            {
                ["Geoprocessing:OutputStaging:Enabled"] = "true",
                ["Geoprocessing:OutputStaging:Provider"] = "local",
                ["Geoprocessing:OutputStaging:LocalRootPath"] = root,
                ["Geoprocessing:OutputStaging:StoreReference"] = "gp-outputs",
                ["Geoprocessing:OutputStaging:MaxInlineArtifactBytes"] = "65536",
            });

            var store = provider.GetService<IGeoprocessingOutputObjectStore>();
            store.Should().NotBeNull();
            store!.Provider.Should().Be(CloudStorageProvider.Local);
            store.StoreReference.Should().Be("gp-outputs");

            var options = provider.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;
            options.MaxInlineArtifactBytes.Should().Be(65536);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisabledStaging_RegistersNoStore()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "false",
        });

        provider.GetService<IGeoprocessingOutputObjectStore>().Should().BeNull();
    }

    [Fact]
    public void EnabledUnsupportedProvider_FailsClosedOnOptionsResolution()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:Provider"] = "s3",
            ["Geoprocessing:OutputStaging:StoreReference"] = "gp-outputs",
        });

        var act = () => provider.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Provider");
    }

    [Fact]
    public void EnabledLocalWithoutRoot_FailsClosedOnOptionsResolution()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:Provider"] = "local",
        });

        var act = () => provider.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("LocalRootPath");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddGeoprocessingOutputStaging(configuration);
        return services.BuildServiceProvider();
    }
}
