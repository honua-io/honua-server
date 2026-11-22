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
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class QueryFormatterSimplificationTests
{
    [UnitTest]
    public async Task FormatQueryResultAsync_Json_OgcWkbWithMaxAllowableOffset_SimplifiesInLayerSrid()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);
        var feature = Feature.Create(1, CreateWiggleOgcWkb(), ImmutableDictionary<string, object?>.Empty);

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            CreatePolylineLayer("polyline-simplify-layer"),
            format: "json",
            returnGeometry: true,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: 100d);

        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Features.Should().ContainSingle();
        queryResponse.Features[0].Geometry!.Paths.Should().ContainSingle().Which.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task StreamAsGeoJsonAsync_OgcWkbWithMaxAllowableOffset_SimplifiesInWgs84()
    {
        var streamingFormatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var feature = Feature.Create(1, CreateWiggleOgcWkb(), ImmutableDictionary<string, object?>.Empty);
        var pipe = new Pipe();

        await streamingFormatter.StreamAsGeoJsonAsync(
            SingleFeatureAsync(feature),
            CreatePolylineLayer("geojson-stream-simplify-layer"),
            returnGeometry: true,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: 100d,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe.Writer);
        await pipe.Writer.CompleteAsync();

        using var stream = pipe.Reader.AsStream();
        using var document = await JsonDocument.ParseAsync(stream);
        var coordinates = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry")
            .GetProperty("coordinates");
        coordinates.GetArrayLength().Should().Be(2);
    }

    private static MetadataV2Object CreatePolylineLayer(string name)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name },
            SchemaFields =
            [
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.LineString,
                PrimaryGeometryField = "shape"
            }
        };

    private static byte[] CreateWiggleOgcWkb()
    {
        var line = new GeometryFactory().CreateLineString(
            Enumerable.Range(0, 20).Select(static i => new Coordinate(i * 0.001d, (i % 2) * 0.0001d)).ToArray());
        return new WKBWriter().Write(line);
    }

    private static async IAsyncEnumerable<Feature> SingleFeatureAsync(Feature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }
}
