// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class GPServerEsriOutputTranslationTests
{
    [UnitTest]
    public void Translate_NonemptyOutput_PreservesDeclaredTypesAndValues()
    {
        const string declared = """
            {"fields":[
              {"name":"key","type":"esriFieldTypeOID"},
              {"name":"count","type":"esriFieldTypeInteger"},
              {"name":"observed","type":"esriFieldTypeDate"},
              {"name":"globalid","type":"esriFieldTypeGUID"},
              {"name":"missing","type":"esriFieldTypeInteger"}]}
            """;
        const string input = """
            {"type":"FeatureCollection","features":[{"type":"Feature",
             "properties":{"key":701,"count":12,"observed":1704067200000,
               "globalid":"{12345678-1234-1234-1234-123456789ABC}","missing":null},
             "geometry":{"type":"Point","coordinates":[1,2,3]}}]}
            """;
        // Simulate one new computed field while preserving the input columns.
        var output = input.Replace("\"properties\":{", "\"properties\":{\"area\":8,", StringComparison.Ordinal);
        var schema = GPServerEsriOutputTranslation.DescribeInput(DataUri(input), declared);
        var result = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer,
            DataUri(output), 4326, schema.GetRawText());

        var fields = result.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("name").GetString()!, field => field.GetProperty("type").GetString());
        fields["key"].Should().Be("esriFieldTypeInteger");
        fields["count"].Should().Be("esriFieldTypeInteger");
        fields["observed"].Should().Be("esriFieldTypeDate");
        fields["globalid"].Should().Be("esriFieldTypeGUID");
        fields["missing"].Should().Be("esriFieldTypeInteger");
        fields["area"].Should().Be("esriFieldTypeDouble");
        fields[result.GetProperty("objectIdFieldName").GetString()!].Should().Be("esriFieldTypeOID");
        var feature = result.GetProperty("features")[0];
        var attributes = feature.GetProperty("attributes");
        attributes.GetProperty("key").GetInt32().Should().Be(701);
        attributes.GetProperty("count").GetInt32().Should().Be(12);
        attributes.GetProperty("observed").GetInt64().Should().Be(1704067200000);
        attributes.GetProperty("globalid").GetString().Should().Be("{12345678-1234-1234-1234-123456789ABC}");
        attributes.GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        attributes.GetProperty("area").GetDouble().Should().Be(8);
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(2);
        feature.GetProperty("geometry").GetProperty("z").GetDouble().Should().Be(3);
        result.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        result.GetProperty("hasM").GetBoolean().Should().BeFalse();
        result.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private static string DataUri(string value) =>
        "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
