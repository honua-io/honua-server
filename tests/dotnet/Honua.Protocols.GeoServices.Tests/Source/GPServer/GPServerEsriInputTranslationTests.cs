// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Unit coverage for the additive ArcGIS-compatible input translation: Esri
/// FeatureSet / esriGeometry JSON inputs are rewritten to the canonical
/// base64-WKB + srid contract, while native string / base64-WKB inputs pass
/// through untouched.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerEsriInputTranslationTests
{
    [UnitTest]
    public void Translate_NativeStringAndBase64Inputs_PassThroughUnchanged()
    {
        const string wkb = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wkb"] = wkb,
            ["srid"] = "4326",
            ["distance"] = "25.5"
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeFalse();
        result.CapabilityMessage.Should().BeNull();
        result.Inputs["wkb"].Should().Be(wkb);
        result.Inputs["srid"].Should().Be("4326");
        result.Inputs["distance"].Should().Be("25.5");
    }

    [UnitTest]
    public void Translate_EsriGeometryPoint_RewritesToBase64WkbAndDerivesSrid()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wkb"] = """{"x":-118.15,"y":33.80,"spatialReference":{"wkid":4326}}""",
            ["distance"] = "10"
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeTrue();
        result.CapabilityMessage.Should().BeNull();
        result.InputSpatialReference.Should().Be(4326);
        // The derived srid is surfaced as the canonical 'srid' input.
        result.Inputs["srid"].Should().Be("4326");

        var geometry = DecodeWkb(result.Inputs["wkb"]);
        geometry.Should().BeOfType<Point>();
        var point = (Point)geometry;
        point.X.Should().BeApproximately(-118.15, 1e-9);
        point.Y.Should().BeApproximately(33.80, 1e-9);
    }

    [UnitTest]
    public void Translate_EsriPolygonRings_RewritesToPolygonWkb()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["targetWkb"] =
                """{"rings":[[[0,0],[0,10],[10,10],[10,0],[0,0]]],"spatialReference":{"wkid":3857}}""",
            ["srid"] = "3857"
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeTrue();
        var geometry = DecodeWkb(result.Inputs["targetWkb"]);
        geometry.Should().BeAssignableTo<Polygon>();
        geometry.Area.Should().BeApproximately(100.0, 1e-6);
        // Existing explicit srid input is preserved (not overwritten).
        result.Inputs["srid"].Should().Be("3857");
    }

    [UnitTest]
    public void Translate_SingleFeatureFeatureSet_RewritesGeometryAndDerivesSrid()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wkb"] =
                """
                {
                  "geometryType": "esriGeometryPoint",
                  "spatialReference": { "wkid": 4326 },
                  "features": [ { "geometry": { "x": 1.0, "y": 2.0 }, "attributes": { "id": 1 } } ]
                }
                """,
            ["distance"] = "5"
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeTrue();
        result.RequiresFeatureCollectionExecution.Should().BeFalse();
        result.InputSpatialReference.Should().Be(4326);

        var geometry = DecodeWkb(result.Inputs["wkb"]);
        geometry.Should().BeOfType<Point>();
        ((Point)geometry).X.Should().BeApproximately(1.0, 1e-9);
    }

    [UnitTest]
    public void Translate_MultiFeatureFeatureSet_SurfacesFeatureCollectionCapabilityMessage()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wkb"] =
                """
                {
                  "geometryType": "esriGeometryPoint",
                  "spatialReference": { "wkid": 4326 },
                  "features": [
                    { "geometry": { "x": 1.0, "y": 2.0 } },
                    { "geometry": { "x": 3.0, "y": 4.0 } }
                  ]
                }
                """
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeFalse();
        result.RequiresFeatureCollectionExecution.Should().BeTrue();
        result.CapabilityMessage.Should().NotBeNull();
        result.CapabilityMessage.Should().Contain("feature-collection");
        result.CapabilityMessage.Should().Contain("2 features");
    }

    [UnitTest]
    public void Translate_FeatureSetMissingGeometry_SurfacesCapabilityMessage()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wkb"] = """{ "features": [ { "attributes": { "id": 1 } } ] }"""
        };

        var result = GPServerEsriInputTranslation.Translate(inputs);

        result.Translated.Should().BeFalse();
        result.CapabilityMessage.Should().Contain("geometry");
    }

    private static Geometry DecodeWkb(string base64)
    {
        var reader = new WKBReader { HandleSRID = true };
        return reader.Read(Convert.FromBase64String(base64));
    }
}
