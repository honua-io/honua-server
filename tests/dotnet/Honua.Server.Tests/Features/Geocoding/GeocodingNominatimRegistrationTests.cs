// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Integration;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LibraryGeocodeProvider = Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeProvider;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class GeocodingNominatimRegistrationTests
{
    [UnitTest]
    public void AddGeocoding_NominatimEnabledAbsent_RegistersNominatim()
    {
        var services = CreateServices(new Dictionary<string, string?>());

        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(LibraryGeocodeProvider));
    }

    [UnitTest]
    public void AddGeocoding_NominatimExplicitlyDisabled_DoesNotRegisterNominatim()
    {
        var services = CreateServices(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:Nominatim:Enabled"] = "false"
        });

        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType == typeof(LibraryGeocodeProvider));
    }

    [UnitTest]
    public async Task CreateHardenedGeocodingHandler_DisablesRedirectsProxyCookies_AndBlocksPrivateDestinations()
    {
        using var handler = ProviderIntegrationExtensions.CreateHardenedGeocodingHandler(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);

        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://geocoder.example.test/search"));
        Assert.Contains("disallowed network address", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task AddGeocoding_ResolvedNominatimProvider_RoutesRequestsThroughHardenedNamedHttpClient()
    {
        var services = CreateServices(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:Nominatim:BaseUrl"] = "https://93.184.216.34"
        });
        var probe = new HandlerProbe();
        services.AddHttpClient(ProviderIntegrationExtensions.NominatimClientName)
            .AddHttpMessageHandler(() => new RewriteToLoopbackHandler(probe));

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<LibraryGeocodeProvider>();

        var ex = await Assert.ThrowsAsync<GeocodeProviderException>(
            () => provider.ForwardGeocodeAsync(new Honua.Geocoding.Features.Geocoding.Domain.ForwardGeocodeRequest("Honolulu")));

        Assert.True(probe.Calls > 0);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Contains("disallowed network address", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task AddAzureMapsGeocodeProvider_ResolvedProvider_RoutesRequestsThroughHardenedNamedHttpClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Geocoding:Providers:AzureMaps:BaseUrl"] = "https://93.184.216.34",
                ["Geocoding:Providers:AzureMaps:SubscriptionKey"] = "test-subscription-key"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureMapsGeocodeProvider(configuration);
        var probe = new HandlerProbe();
        services.AddHttpClient(ProviderIntegrationExtensions.AzureMapsClientName)
            .AddHttpMessageHandler(() => new RewriteToLoopbackHandler(probe));

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<LibraryGeocodeProvider>();

        var ex = await Assert.ThrowsAsync<GeocodeProviderException>(
            () => provider.ForwardGeocodeAsync(new Honua.Geocoding.Features.Geocoding.Domain.ForwardGeocodeRequest("Honolulu")));

        Assert.True(probe.Calls > 0);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Contains("disallowed network address", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceCollection CreateServices(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeocoding(configuration);
        return services;
    }

    private sealed class HandlerProbe
    {
        public int Calls { get; set; }
    }

    private sealed class RewriteToLoopbackHandler(HandlerProbe probe) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            probe.Calls++;
            request.RequestUri = new Uri("https://127.0.0.1/search");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
