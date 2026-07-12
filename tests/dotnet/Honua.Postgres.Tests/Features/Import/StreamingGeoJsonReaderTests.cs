// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class StreamingGeoJsonReaderTests
{
    [Fact]
    public async Task ReadFeaturesAsync_WithNestedObjectAndArrayProperties_PreservesStructuredJson()
    {
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "geometry": {
                    "type": "Point",
                    "coordinates": [-157.8583, 21.3069]
                  },
                  "properties": {
                    "name": "Honolulu Harbor",
                    "metadata": {
                      "depth": 42,
                      "active": true
                    },
                    "tags": ["port", {"kind": "commercial"}]
                  }
                }
              ]
            }
            """;

        var reader = new StreamingGeoJsonReader();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(geoJson));

        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in reader.ReadFeaturesAsync(stream))
        {
            features.Add(feature);
        }

        features.Should().ContainSingle();
        var attributes = features[0].Attributes;
        attributes.Should().NotBeNull();

        attributes["name"].Should().Be("Honolulu Harbor");

        var metadata = attributes["metadata"].Should().BeOfType<JsonElement>().Subject;
        metadata.ValueKind.Should().Be(JsonValueKind.Object);
        metadata.GetProperty("depth").GetInt64().Should().Be(42);
        metadata.GetProperty("active").GetBoolean().Should().BeTrue();

        var tags = attributes["tags"].Should().BeOfType<JsonElement>().Subject;
        tags.ValueKind.Should().Be(JsonValueKind.Array);
        tags[0].GetString().Should().Be("port");
        tags[1].GetProperty("kind").GetString().Should().Be("commercial");
    }

    [Fact]
    public async Task ValidateAsync_WithSelfIntersectingPolygon_ReportsNonFatalRepairWarning()
    {
        // A bowtie polygon is topologically invalid but structurally well-formed. The validate/
        // preview surface must report it as a NON-fatal, row-level warning (repaired on import by
        // the shared validity gate) rather than failing the whole file (#2743).
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature",
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[2,2],[2,0],[0,2],[0,0]]] },
                  "properties": { "name": "bowtie" } }
              ]
            }
            """;

        var reader = new StreamingGeoJsonReader();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(geoJson));

        var result = await reader.ValidateAsync(stream);

        // Warning severity does not fail the document.
        result.IsValid.Should().BeTrue();
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Code.Should().Be(ImportValidationErrorCodes.GeometryInvalidRepairable);
        issue.Severity.Should().Be(ImportValidationSeverity.Warning);
        issue.FeatureIndex.Should().Be(1);
    }

    [Fact]
    public async Task ValidateAsync_WithValidPolygon_ReportsNoIssues()
    {
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature",
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[0,2],[2,2],[2,0],[0,0]]] },
                  "properties": { "name": "square" } }
              ]
            }
            """;

        var reader = new StreamingGeoJsonReader();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(geoJson));

        var result = await reader.ValidateAsync(stream);

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ImportJsonContext_DictionaryStringObject_SerializesNestedJsonElements()
    {
        using var metadataDocument = JsonDocument.Parse("""{"depth":42,"active":true}""");
        using var tagsDocument = JsonDocument.Parse("""["port",{"kind":"commercial"}]""");

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["metadata"] = metadataDocument.RootElement.Clone(),
            ["tags"] = tagsDocument.RootElement.Clone()
        };

        var json = JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("metadata").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("metadata").GetProperty("depth").GetInt64().Should().Be(42);
        root.GetProperty("tags").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("tags")[1].GetProperty("kind").GetString().Should().Be("commercial");
    }
}
