// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class EsriGeocodeAddressFieldsTests
{
    [UnitTest]
    public void Apply_FillsTheAddressFieldsArcpyMaps_AndKeepsProviderKeys()
    {
        var attributes = EsriGeocodeAddressFields.Apply(
            "10 Main St, Honolulu, HI 96813",
            "PointAddress",
            new StructuredAddress
            {
                AddressNumber = "10",
                StreetName = "Main St",
                City = "Honolulu",
                Region = "Hawaii",
                PostalCode = "96813",
                CountryCode = "us",
                Neighborhood = "Downtown"
            },
            new Dictionary<string, string?>
            {
                ["display_name"] = "10 Main St, Honolulu, HI 96813",
                ["osm_id"] = "1"
            });

        attributes["Addr_type"].Should().Be("PointAddress");
        attributes["Match_addr"].Should().Be("10 Main St, Honolulu, HI 96813");
        attributes["Address"].Should().Be("10 Main St");
        attributes["AddNum"].Should().Be("10");
        attributes["City"].Should().Be("Honolulu");
        attributes["Region"].Should().Be("Hawaii");
        attributes["Postal"].Should().Be("96813");
        attributes["CountryCode"].Should().Be("us");
        attributes["Neighborhood"].Should().Be("Downtown");
        attributes["display_name"].Should().Be("10 Main St, Honolulu, HI 96813");
        attributes["osm_id"].Should().Be("1");
    }

    // #5145: a candidate carries the output-schema fields the geocoding tools read off the
    // result - Loc_name, Status and Score - which a reverse-geocode address does not.
    [UnitTest]
    public void ApplyCandidate_StampsTheOutputSchema_WithNumericScore()
    {
        var matched = EsriGeocodeAddressFields.ApplyCandidate(
            "10 Main St, Honolulu, HI 96813",
            "PointAddress",
            new StructuredAddress { AddressNumber = "10", StreetName = "Main St" },
            new Dictionary<string, string?>(),
            score: 92.5,
            locatorName: "nominatim",
            isMatch: true);

        matched["Status"].Text.Should().Be("M");
        matched["Loc_name"].Text.Should().Be("nominatim");
        matched["Provider"].Text.Should().Be("nominatim");
        matched["Score"].Number.Should().Be(92.5);
        matched["Score"].Text.Should().BeNull("candidateFields declares Score as esriFieldTypeDouble");
        matched["Match_addr"].Text.Should().Be("10 Main St, Honolulu, HI 96813");
    }

    // A no-match slot in a batch response is "U". It is derived from whether the record
    // produced a location, never asserted.
    [UnitTest]
    public void ApplyCandidate_MarksAnUnmatchedSlotUnmatched()
    {
        var unmatched = EsriGeocodeAddressFields.ApplyCandidate(
            matchedAddress: null,
            addressType: null,
            structured: null,
            new Dictionary<string, string?>(),
            score: 0,
            locatorName: "nominatim",
            isMatch: false);

        unmatched["Status"].Text.Should().Be("U");
        unmatched["Score"].Number.Should().Be(0);
    }
}
