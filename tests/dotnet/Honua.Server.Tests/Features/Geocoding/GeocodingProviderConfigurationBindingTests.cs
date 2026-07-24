// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// Regression tests for honua-server's provider configuration binding. The provider
/// configuration records previously used init-only properties, which the configuration
/// binding source generator (EnableConfigurationBindingGenerator, required for the
/// native-AOT Lambda image) cannot set — binding silently produced empty configurations,
/// so an env-configured Amazon Location provider failed at construction with
/// "Amazon Location place index name is required" even though
/// Geocoding__Providers__AmazonLocation__PlaceIndexName was set (observed live on
/// demo.honua.io, honua-server#2948). These tests bind through the same
/// AddOptions().Bind() path the providers use.
/// </summary>
public sealed class GeocodingProviderConfigurationBindingTests
{
    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AmazonLocationConfiguration_BindsAllProperties()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:AmazonLocation:Enabled"] = "true",
            ["Geocoding:Providers:AmazonLocation:Region"] = "us-west-2",
            ["Geocoding:Providers:AmazonLocation:PlaceIndexName"] = "honua-demo-demo-geocode",
            ["Geocoding:Providers:AmazonLocation:UseIamRole"] = "true",
            ["Geocoding:Providers:AmazonLocation:MaxResults"] = "10",
            ["Geocoding:Providers:AmazonLocation:TimeoutSeconds"] = "20",
        });

        var services = new ServiceCollection();
        services.AddOptions<AmazonLocationProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:AmazonLocation"));
        using var provider = services.BuildServiceProvider();

        var bound = provider.GetRequiredService<IOptionsMonitor<AmazonLocationProviderConfiguration>>().CurrentValue;

        Assert.True(bound.Enabled);
        Assert.Equal("us-west-2", bound.Region);
        Assert.Equal("honua-demo-demo-geocode", bound.PlaceIndexName);
        Assert.True(bound.UseIamRole);
        Assert.Equal(10, bound.MaxResults);
        Assert.Equal(20, bound.TimeoutSeconds);
    }

    [Fact]
    public void NominatimConfiguration_BindsBaseAndProviderProperties()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:Nominatim:Enabled"] = "false",
            ["Geocoding:Providers:Nominatim:BaseUrl"] = "https://nominatim.internal.example",
            ["Geocoding:Providers:Nominatim:MaxBatchSize"] = "25",
        });

        var services = new ServiceCollection();
        services.AddOptions<NominatimProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:Nominatim"));
        using var provider = services.BuildServiceProvider();

        var bound = provider.GetRequiredService<IOptionsMonitor<NominatimProviderConfiguration>>().CurrentValue;

        Assert.False(bound.Enabled);
        Assert.Equal("https://nominatim.internal.example", bound.BaseUrl);
        Assert.Equal(25, bound.MaxBatchSize);
    }

    [Fact]
    public void AzureMapsConfiguration_BindsSubscriptionKey()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:AzureMaps:Enabled"] = "true",
            ["Geocoding:Providers:AzureMaps:SubscriptionKey"] = "test-key",
        });

        var services = new ServiceCollection();
        services.AddOptions<AzureMapsProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:AzureMaps"));
        using var provider = services.BuildServiceProvider();

        var bound = provider.GetRequiredService<IOptionsMonitor<AzureMapsProviderConfiguration>>().CurrentValue;

        Assert.True(bound.Enabled);
        Assert.Equal("test-key", bound.SubscriptionKey);
    }
}
