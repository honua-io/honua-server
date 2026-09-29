// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class FeatureServiceUnitsTests
{
    // Esri's Feature Service reference response pairs EPSG:4326 with esriDecimalDegrees.
    // https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/
    // Other geographic CRSs and projected metre CRSs are independent controls.
    [UnitTheory]
    [InlineData(null, null, 4326, "esriDecimalDegrees")]
    [InlineData(4326, null, 4326, "esriDecimalDegrees")]
    [InlineData(4326, null, 4326, "esriDecimalDegrees", true)]
    [InlineData(4269, null, 4269, "esriDecimalDegrees")]
    [InlineData(4979, null, 4979, "esriDecimalDegrees")]
    [InlineData(null, "http://www.opengis.net/def/crs/EPSG/0/4258", 4258, "esriDecimalDegrees")]
    [InlineData(null, "urn:ogc:def:crs:OGC:1.3:CRS84", 4326, "esriDecimalDegrees")]
    [InlineData(3857, null, 3857, "esriMeters")]
    [InlineData(26915, null, 26915, "esriMeters")]
    [InlineData(4978, null, 4978, "esriMeters")]
    public void ServiceMetadata_DeclaredOrDefaultCrs_UsesMatchingUnits(int? srid, string? crs, int expectedSrid, string expectedUnits, bool isGeographic = false)
    {
        var service = new MetadataV2Service
        {
            SpatialReference = srid.HasValue || crs is not null
                ? new MetadataV2SpatialReference { Srid = srid, Crs = crs, IsGeographic = isGeographic }
                : null,
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph(), "units-test", DateTimeOffset.UnixEpoch);

        var response = FeatureServerEndpoints.MapServiceToResponseV2(service, [], snapshot,
            new QueryLimits(), false, false, false, false, offlineSyncEnabled: false);

        response.SpatialReference.Wkid.Should().Be(expectedSrid);
        response.Units.Should().Be(expectedUnits);
    }
}
