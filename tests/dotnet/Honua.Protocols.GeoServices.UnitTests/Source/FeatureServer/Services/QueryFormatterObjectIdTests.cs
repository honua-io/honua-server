// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class QueryFormatterObjectIdTests
{
    public static IEnumerable<object[]> Projections()
    {
        foreach (var streaming in new[] { false, true })
        foreach (var format in new[] { "json", "geojson" })
        foreach (var fields in new[] { "*", "id,name" })
        foreach (var source in new[] { "null", "json-null", "missing", "zero", "present" })
        {
            yield return [streaming, format, fields, source];
        }
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(Projections))]
    public async Task Query_SourceObjectId_UsesCanonicalFallbackOnlyForNullOrMissing(
        bool streaming, string format, string fields, string source)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = "alpha" };
        if (source != "missing")
        {
            attributes["id"] = source switch
            {
                "null" => null,
                "json-null" => JsonSerializer.SerializeToElement<object?>(null),
                "zero" => 0L,
                _ => 123L
            };
        }
        var feature = Feature.Create(42, null, attributes.ToImmutableDictionary());
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "id-layer", Name = "id-layer" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };
        var json = await SerializeAsync(feature, resource, streaming, format, fields.Split(','));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("features")[0];
        var properties = result.GetProperty(format == "json" ? "attributes" : "properties");
        var expectedId = source switch { "zero" => 0L, "present" => 123L, _ => 42L };
        properties.GetProperty("id").GetInt64().Should().Be(expectedId, json);
        properties.GetProperty("name").GetString().Should().Be("alpha");
        if (format == "geojson")
        {
            result.GetProperty("id").GetInt64().Should().Be(expectedId, json);
        }
    }

    private static async Task<string> SerializeAsync(
        Feature feature, MetadataV2Object resource, bool streaming, string format, string[] fields)
    {
        var limits = Options.Create(new LimitsOptions());
        if (!streaming)
        {
            var formatter = new QueryFormatter(limits, new PbfQueryFormatter(limits), NullLogger<QueryFormatter>.Instance);
            var (response, _) = await formatter.FormatQueryResultAsync(
                QueryResult<Feature>.Create(1, [feature]), resource, format: format,
                returnGeometry: false, outputSrid: null, returnZ: false, returnM: false,
                geometryPrecision: null, maxAllowableOffset: null, outFields: fields);
            return format == "json"
                ? JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse)
                : JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.GeoJsonFeatureSet);
        }

        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        var streamingFormatter = new StreamingQueryFormatter(limits);
        if (format == "json")
        {
            await streamingFormatter.StreamAsGeoServicesJsonAsync(
                Features(), resource, returnGeometry: false, outputSrid: null, returnZ: false, returnM: false,
                geometryPrecision: null, maxAllowableOffset: null, outFields: fields,
                hasMoreResults: false, outputStream: writer);
        }
        else
        {
            await streamingFormatter.StreamAsGeoJsonAsync(
                Features(), resource, returnGeometry: false, returnZ: false, returnM: false,
                geometryPrecision: null, maxAllowableOffset: null, outFields: fields,
                hasMoreResults: false, outputStream: writer);
        }
        await writer.CompleteAsync();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());

        async IAsyncEnumerable<Feature> Features()
        {
            yield return feature;
            await Task.CompletedTask;
        }
    }
}
