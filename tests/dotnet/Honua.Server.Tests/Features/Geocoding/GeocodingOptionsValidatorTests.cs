// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class GeocodingOptionsValidatorTests
{
    private readonly GeocodingOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_WithPublicHttpsBaseUrl_ReturnsSuccess()
    {
        // Literal public IP avoids depending on DNS in the unit-test process.
        // OutboundHttpUrlValidatorTests cover the DNS resolution path independently.
        var options = CreateOptions("https://8.8.8.8/nominatim");

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public void Validate_WithInsecureBaseUrl_ReturnsFailure()
    {
        var options = CreateOptions("http://nominatim.example");

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("BaseUrl") &&
            failure.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_WithPrivateBaseUrl_ReturnsFailure()
    {
        var options = CreateOptions("https://127.0.0.1:8080");

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("BaseUrl") &&
            failure.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    private static GeocodingOptions CreateOptions(string baseUrl)
        => new()
        {
            LocatorName = "World",
            DefaultProvider = "nominatim",
            DefaultSpatialReferenceWkid = 4326,
            Nominatim = new NominatimGeocodingOptions
            {
                BaseUrl = baseUrl,
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                DefaultMaxResults = 10,
                DefaultMaxSuggestions = 5
            }
        };
}
