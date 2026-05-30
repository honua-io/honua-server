// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Unit coverage for honoring <c>env:outSR</c> on synchronous results by
/// reprojecting GeoJSON output artifacts, and for the capability messaging
/// returned when a transform or output type is unsupported.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerOutputReprojectionTests
{
    private const string GeoJsonDataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public void TryReproject_Wgs84ToWebMercator_RewritesCoordinates()
    {
        // A point at (lon -118.15, lat 33.80) in WGS84.
        var feature = BuildFeatureDataUri(new Point(-118.15, 33.80));

        var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(feature, fromSrid: 4326, outSrid: 3857);

        outcome.Reprojected.Should().BeTrue();
        outcome.CapabilityMessage.Should().BeNull();

        var geometry = (Point)DecodeFeatureGeometry(outcome.Value!);
        // Web Mercator (spherical) easting/northing for lon -118.15, lat 33.80.
        geometry.X.Should().BeApproximately(-13152400.0, 5000.0);
        geometry.Y.Should().BeApproximately(4001978.0, 5000.0);
    }

    [UnitTest]
    public void TryReproject_IdentityTransform_ReturnsInputUnchanged()
    {
        var feature = BuildFeatureDataUri(new Point(10, 20));

        var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(feature, fromSrid: 4326, outSrid: 4326);

        outcome.Reprojected.Should().BeTrue();
        outcome.Value.Should().Be(feature);
    }

    [UnitTest]
    public void TryReproject_UnsupportedDatumShiftPair_ReturnsCapabilityMessage()
    {
        var feature = BuildFeatureDataUri(new Point(10, 20));

        var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(feature, fromSrid: 4326, outSrid: 2193);

        outcome.Reprojected.Should().BeFalse();
        outcome.CapabilityMessage.Should().Contain("not supported");
        // Value is preserved (unreprojected) rather than dropped.
        outcome.Value.Should().Be(feature);
    }

    [UnitTest]
    public void TryReproject_NonGeoJsonValue_ReturnsCapabilityMessage()
    {
        const string httpUri = "https://example.test/output.geojson";

        var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(httpUri, fromSrid: 4326, outSrid: 3857);

        outcome.Reprojected.Should().BeFalse();
        outcome.CapabilityMessage.Should().Contain("not a reprojectable");
        outcome.Value.Should().Be(httpUri);
    }

    [UnitTest]
    public void TryReproject_UnknownWorkingSrid_ReturnsCapabilityMessage()
    {
        var feature = BuildFeatureDataUri(new Point(10, 20));

        var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(feature, fromSrid: 0, outSrid: 3857);

        outcome.Reprojected.Should().BeFalse();
        outcome.CapabilityMessage.Should().Contain("input spatial reference is unknown");
    }

    private static string BuildFeatureDataUri(Geometry geometry)
    {
        var geometryJson = new GeoJsonWriter().Write(geometry);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WritePropertyName("geometry");
            using (var doc = JsonDocument.Parse(geometryJson))
            {
                doc.RootElement.WriteTo(writer);
            }

            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WriteString("processId", "geometry.test");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return GeoJsonDataUriPrefix + Convert.ToBase64String(buffer.ToArray());
    }

    private static Geometry DecodeFeatureGeometry(string dataUri)
    {
        var base64 = dataUri[GeoJsonDataUriPrefix.Length..];
        var featureJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        using var doc = JsonDocument.Parse(featureJson);
        var geometryJson = doc.RootElement.GetProperty("geometry").GetRawText();
        return new GeoJsonReader().Read<Geometry>(geometryJson);
    }
}
