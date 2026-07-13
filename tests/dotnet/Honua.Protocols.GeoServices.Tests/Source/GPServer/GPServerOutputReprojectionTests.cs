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

    // Hand-written geometry JSON: NTS's GeoJsonWriter enforces RFC 7946 winding on write, so
    // serializing a clockwise NTS polygon through it would silently pre-normalize the payload
    // and make the winding tests vacuous. The raw JSON keeps the wrong (clockwise) orientation.
    private const string ClockwisePolygonJson =
        """{"type":"Polygon","coordinates":[[[0.0,0.0],[0.0,1.0],[1.0,1.0],[1.0,0.0],[0.0,0.0]]]}""";

    [UnitTest]
    public void NormalizeGeoJsonWinding_ClockwiseExterior_RewritesToRightHandRule()
    {
        var feature = BuildFeatureDataUri(ClockwisePolygonJson);

        var normalized = GPServerOutputReprojection.NormalizeGeoJsonWinding(feature);

        normalized.Should().NotBe(feature, "a clockwise exterior ring must be rewound");
        var polygon = (Polygon)DecodeFeatureGeometry(normalized!);
        NetTopologySuite.Algorithm.Orientation.IsCCW(polygon.ExteriorRing.CoordinateSequence).Should().BeTrue();

        // Non-geometry Feature members must survive the rewrite.
        var base64 = normalized![GeoJsonDataUriPrefix.Length..];
        var featureJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        using var doc = JsonDocument.Parse(featureJson);
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString().Should().Be("geometry.test");
    }

    [UnitTest]
    public void NormalizeGeoJsonWinding_AlreadyRightHandRule_ReturnsInputUnchanged()
    {
        // Ring listed counter-clockwise — already the right-hand rule, so the bytes are untouched.
        const string ccwJson =
            """{"type":"Polygon","coordinates":[[[0.0,0.0],[1.0,0.0],[1.0,1.0],[0.0,1.0],[0.0,0.0]]]}""";
        var feature = BuildFeatureDataUri(ccwJson);

        GPServerOutputReprojection.NormalizeGeoJsonWinding(feature).Should().Be(feature);
    }

    [UnitTest]
    public void NormalizeGeoJsonWinding_NonGeoJsonValue_ReturnsInputUnchanged()
    {
        const string httpUri = "https://example.test/output.geojson";

        GPServerOutputReprojection.NormalizeGeoJsonWinding(httpUri).Should().Be(httpUri);
    }

    [UnitTest]
    public void NormalizeGeoJsonWinding_FeatureCollectionWithClockwiseExterior_RewritesToRightHandRule()
    {
        // Layer/overlay tools emit FeatureCollection data URIs (FeatureCollectionArtifact), not a
        // single Feature; each features[*].geometry must be normalized on the non-reprojected path.
        var collection = BuildFeatureCollectionDataUri(
            ClockwisePolygonJson,
            """{"type":"Point","coordinates":[5.0,5.0]}""");

        var normalized = GPServerOutputReprojection.NormalizeGeoJsonWinding(collection);

        normalized.Should().NotBe(collection);
        using var doc = DecodeDataUri(normalized!);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(2);

        var polygon = (Polygon)new GeoJsonReader().Read<Geometry>(
            features[0].GetProperty("geometry").GetRawText());
        NetTopologySuite.Algorithm.Orientation.IsCCW(polygon.ExteriorRing.CoordinateSequence).Should().BeTrue();

        // The non-polygon sibling feature and all non-geometry members must survive the rewrite.
        features[1].GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        features[0].GetProperty("properties").GetProperty("index").GetInt32().Should().Be(0);
        features[1].GetProperty("properties").GetProperty("index").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
    }

    [UnitTest]
    public void NormalizeGeoJsonWinding_FeatureCollectionAlreadyRightHandRule_ReturnsInputUnchanged()
    {
        var collection = BuildFeatureCollectionDataUri(
            """{"type":"Polygon","coordinates":[[[0.0,0.0],[1.0,0.0],[1.0,1.0],[0.0,1.0],[0.0,0.0]]]}""",
            """{"type":"Point","coordinates":[5.0,5.0]}""");

        GPServerOutputReprojection.NormalizeGeoJsonWinding(collection).Should().Be(collection);
    }

    private static string BuildFeatureDataUri(Geometry geometry) =>
        BuildFeatureDataUri(new GeoJsonWriter().Write(geometry));

    private static string BuildFeatureDataUri(string geometryJson)
    {
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

    private static string BuildFeatureCollectionDataUri(params string[] geometryJsons)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            for (var i = 0; i < geometryJsons.Length; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");
                writer.WritePropertyName("geometry");
                using (var doc = JsonDocument.Parse(geometryJsons[i]))
                {
                    doc.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return GeoJsonDataUriPrefix + Convert.ToBase64String(buffer.ToArray());
    }

    private static JsonDocument DecodeDataUri(string dataUri)
    {
        var base64 = dataUri[GeoJsonDataUriPrefix.Length..];
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
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
