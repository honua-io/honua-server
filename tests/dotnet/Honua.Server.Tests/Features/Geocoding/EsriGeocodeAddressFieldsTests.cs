// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Server.Features.Geocoding;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class EsriGeocodeAddressFieldsTests
{
    [Fact]
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
}
