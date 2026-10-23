// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

public sealed class GeocodingLocatorNameValidationTests
{
    [UnitTheory]
    [InlineData("City/Street", false)]
    [InlineData("City\\Street", true)]
    [InlineData("City%2FStreet", true)]
    [InlineData("City\n", false)]
    [InlineData("..", false)]
    [InlineData("World", true)]
    [InlineData("Montréal 東京", true)]
    [InlineData("City Locator_1.2-3", true)]
    public void Validate_LocatorName_BothConfigurationsEnforceAddressableNames(string name, bool valid)
        => AssertLocatorValidation(name, valid);

    [UnitTheory]
    [InlineData(128, true)]
    [InlineData(129, true)]
    public void Validate_LocatorName_BothConfigurationsPreserveLongNames(int length, bool valid)
        => AssertLocatorValidation(new string('A', length), valid);

    private static void AssertLocatorValidation(string name, bool valid)
    {
        var canonical = new GeocodingConfiguration { LocatorName = name };
        canonical.Providers.Nominatim.Enabled = false;
        var protocol = new GeocodingOptions { LocatorName = name };
        protocol.Nominatim.BaseUrl = "https://8.8.8.8/nominatim";
        var results = new[]
        {
            new GeocodingConfigurationValidator().Validate(null, canonical),
            new GeocodingOptionsValidator().Validate(null, protocol)
        };
        foreach (var result in results)
        {
            Assert.Equal(valid, result.Succeeded);
            if (!valid)
            {
                Assert.Contains(result.Failures ?? [], failure => failure.Contains("Geocoding:LocatorName", StringComparison.Ordinal));
            }
        }
    }
}
