// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Geocoding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LibraryGeocodeProvider = Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeProvider;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// Nominatim is registered unless a deployment sets
/// <c>Geocoding:Providers:Nominatim:Enabled=false</c> (#6601).
/// </summary>
public sealed class GeocodingNominatimRegistrationTests
{
    [Fact]
    public void AddGeocoding_NominatimEnabledAbsent_RegistersNominatim()
    {
        var services = CreateServices(new Dictionary<string, string?>());

        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(LibraryGeocodeProvider));
    }

    [Fact]
    public void AddGeocoding_NominatimExplicitlyDisabled_DoesNotRegisterNominatim()
    {
        var services = CreateServices(new Dictionary<string, string?>
        {
            ["Geocoding:Providers:Nominatim:Enabled"] = "false"
        });

        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType == typeof(LibraryGeocodeProvider));
    }

    private static ServiceCollection CreateServices(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeocoding(configuration);
        return services;
    }
}
