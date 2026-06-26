// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Providers;
using Npgsql;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Enforces the GeocodeServer provider parity matrix (docs/reference/geocoding/geocode-server-parity.md).
/// Asserting each provider's advertised <see cref="GeocodeProviderCapabilities"/> means a regression
/// that turns a previously-supported capability off fails the build, satisfying the #2153 requirement
/// that the matrix runs in CI and fails on regression.
/// </summary>
public sealed class GeocodeServerParityMatrixTests
{
    public static TheoryData<string, bool, bool, bool, bool, bool> ExpectedMatrix() => new()
    {
        // provider, forward, reverse, suggest, batch, structuredInput
        { GeocodeProviderNames.Nominatim, true, true, true, true, true },
        { GeocodeProviderNames.AzureMaps, true, true, true, false, true },
        { GeocodeProviderNames.AmazonLocation, true, true, true, false, true },
        { GeocodeProviderNames.Local, true, true, true, true, true },
    };

    [Theory]
    [MemberData(nameof(ExpectedMatrix))]
    public void Provider_AdvertisesExpectedCapabilities(
        string providerName,
        bool forward,
        bool reverse,
        bool suggest,
        bool batch,
        bool structuredInput)
    {
        using var harness = CreateProvider(providerName);
        var capabilities = harness.Provider.Capabilities;

        Assert.Equal(providerName, harness.Provider.Name);
        Assert.Equal(forward, capabilities.SupportsForwardGeocode);
        Assert.Equal(reverse, capabilities.SupportsReverseGeocode);
        Assert.Equal(suggest, capabilities.SupportsSuggest);
        Assert.Equal(batch, capabilities.SupportsBatch);
        Assert.Equal(structuredInput, capabilities.SupportsStructuredInput);

        if (structuredInput)
        {
            // Structured-capable providers advertise the same canonical structured fields so callers
            // see consistent structured-input fidelity across providers (Azure/Amazon match
            // Nominatim, #2149).
            Assert.Equal(GeocodeStructuredFields.All(), capabilities.SupportedStructuredFields);
        }
    }

    private static ProviderHarness CreateProvider(string providerName)
    {
        switch (providerName)
        {
            case GeocodeProviderNames.Nominatim:
            {
                var http = NewHttpClient();
                return new ProviderHarness(
                    new NominatimGeocodeProvider(new NominatimProviderConfiguration(), http),
                    httpClient: http);
            }

            case GeocodeProviderNames.AzureMaps:
            {
                var http = NewHttpClient();
                return new ProviderHarness(
                    new AzureMapsGeocodeProvider(
                        new AzureMapsProviderConfiguration { SubscriptionKey = "test-key", BaseUrl = "https://example.com" },
                        http),
                    httpClient: http);
            }

            default:
                return CreateClientProvider(providerName);
        }
    }

    private static ProviderHarness CreateClientProvider(string providerName) => providerName switch
    {
        // Explicit (non-IAM) dummy credentials avoid the default credential-chain lookup so the AWS
        // client constructs offline; no network call is made when only reading Capabilities.
        GeocodeProviderNames.AmazonLocation => new ProviderHarness(
            new AmazonLocationGeocodeProvider(new AmazonLocationProviderConfiguration
            {
                PlaceIndexName = "test-index",
                UseIamRole = false,
                AccessKeyId = "test",
                SecretAccessKey = "test"
            })),

        GeocodeProviderNames.Local => new ProviderHarness(
            new LocalPostgisGeocodeProvider(
                NpgsqlDataSource.Create("Host=localhost;Database=geocode;Username=test;Password=test"),
                new LocalGeocoderProviderConfiguration(),
                ownsDataSource: true)),

        _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider."),
    };

    private static HttpClient NewHttpClient()
        => new() { BaseAddress = new Uri("https://example.com/", UriKind.Absolute) };

    private sealed class ProviderHarness(IGeocodeProvider provider, HttpClient? httpClient = null) : IDisposable
    {
        public IGeocodeProvider Provider { get; } = provider;

        public void Dispose()
        {
            httpClient?.Dispose();
            (Provider as IDisposable)?.Dispose();
        }
    }
}
