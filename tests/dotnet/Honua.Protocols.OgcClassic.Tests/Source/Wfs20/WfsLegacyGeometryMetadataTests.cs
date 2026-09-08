// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
public sealed class WfsLegacyGeometryMetadataTests
{
    private static readonly XNamespace Honua = "http://honua.io/wfs";
    private static readonly XNamespace Gml = "http://www.opengis.net/gml";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    [IntegrationTheory]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0", true)]
    [InlineData("1.1.0", false)]
    [InlineData("1.1.0", true)]
    [Protocol(TestProtocols.Wfs10)]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs10, "DescribeFeatureType")]
    [InterfaceOperation(TestProtocols.Wfs11, "DescribeFeatureType")]
    public async Task GetFeature_GeometryMetadataWithoutSchemaField_PreservesGeometryAndControls(
        string version, bool usePost)
    {
        var queries = new List<FeatureQuery>();
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(BuildMetadataProvider())
            .DecorateService<IFeatureReader>(inner => new RecordingFeatureReader(inner, queries));
        try
        {
            await fixture.InitializeAsync();
            await SeedGeometriesAsync(fixture);
            using var scope = new AssertionScope();

            await AssertGeometryAsync("legacy_point", 5001, "Point", [-122.5, 37.5]);
            await AssertGeometryAsync("legacy_line", 5002, "LineString", [-122.5, 37.5, -122.25, 37.75]);
            await AssertGeometryAsync("legacy_polygon", 5003, "Polygon",
                [-122.5, 37.5, -122.25, 37.5, -122.25, 37.75, -122.5, 37.5]);
            await AssertGeometryAsync("legacy_explicit", 5004, "Point", [-122.5, 37.5], "shape");

            var nullGeometry = await GetFeatureAsync(fixture, version, usePost, "legacy_point", 3);
            nullGeometry.Descendants(Honua + "legacy_point").Should().ContainSingle();
            nullGeometry.Descendants(Honua + "geometry").Should().BeEmpty();
            nullGeometry.Descendants(Honua + "name").Single().Value.Should().Be("Third Feature");

            // Even when storage contains geometry, a nonspatial resource must not expose it.
            var nonspatial = await GetFeatureAsync(fixture, version, usePost, "legacy_table", 5005);
            nonspatial.Descendants(Honua + "legacy_table").Should().ContainSingle();
            nonspatial.Descendants(Honua + "geometry").Should().BeEmpty();
            nonspatial.Descendants(Gml + "Point").Should().BeEmpty();
            var tableSchema = await DescribeAsync(fixture, version, usePost, "legacy_table");
            tableSchema.Descendants(Xsd + "element")
                .Should().NotContain(element => (string?)element.Attribute("name") == "geometry");

            // Adding the missing geometry must not change attribute projection semantics.
            var projected = await GetFeatureAsync(fixture, version, usePost, "legacy_point", 5001, "name");
            queries.Last().ExcludeAttributes.Should().BeFalse();
            projected.Descendants(Honua + "name").Single().Value.Should().Be("Legacy Point");
            projected.Descendants(Honua + "objectid").Should().BeEmpty();

            // A declared multi-geometry must keep its advertised homogeneous type.
            await AssertMultiGeometryAsync("legacy_multipoint", 5006, "Point", 2);
            await AssertMultiGeometryAsync("legacy_multiline", 5007, "LineString", 1);
            await AssertMultiGeometryAsync("legacy_multipolygon", 5008, "Polygon", 1);

            // The advertised fallback geometry property must also be requestable by
            // projection; it resolves against no schema field, so PROPERTYNAME must not
            // reject it as unknown.
            var geometryOnly = await GetFeatureAsync(fixture, version, usePost, "legacy_point", 5001, "geometry");
            queries.Last().ExcludeAttributes.Should().BeTrue("geometry-only queries must not fetch attribute columns");
            geometryOnly.Descendants(Honua + "geometry").Should().ContainSingle()
                .Which.Elements(Gml + "Point").Should().ContainSingle();
            geometryOnly.Descendants(Honua + "name").Should().BeEmpty();
            geometryOnly.Descendants(Honua + "objectid").Should().BeEmpty();

            var geometryAndName = await GetFeatureAsync(
                fixture, version, usePost, "legacy_point", 5001, "honua:geometry,name");
            queries.Last().ExcludeAttributes.Should().BeFalse("mixed projections still need their attributes");
            geometryAndName.Descendants(Honua + "geometry").Should().ContainSingle()
                .Which.Elements(Gml + "Point").Should().ContainSingle();
            geometryAndName.Descendants(Honua + "name").Single().Value.Should().Be("Legacy Point");
            geometryAndName.Descendants(Honua + "objectid").Should().BeEmpty();

            // An explicit geometry field keeps resolving through the existing alias path.
            var explicitProjection = await GetFeatureAsync(
                fixture, version, usePost, "legacy_explicit", 5004, "shape");
            queries.Last().ExcludeAttributes.Should().BeTrue("explicit geometry-only projections also omit attributes");
            explicitProjection.Descendants(Honua + "shape").Should().ContainSingle();
            explicitProjection.Descendants(Honua + "objectid").Should().BeEmpty();

            var aliasProjection = await GetFeatureAsync(
                fixture, version, usePost, "legacy_point", 5001, "shape");
            queries.Last().ExcludeAttributes.Should().BeTrue();
            aliasProjection.Descendants(Honua + "geometry").Should().ContainSingle();
            aliasProjection.Descendants(Honua + "name").Should().BeEmpty();

            var wildcard = await GetFeatureAsync(fixture, version, usePost, "legacy_point", 5001, "*");
            queries.Last().ExcludeAttributes.Should().BeFalse("wildcards retain all attributes");
            wildcard.Descendants(Honua + "name").Single().Value.Should().Be("Legacy Point");

            // A nonspatial resource advertises no geometry property, so requesting one
            // must still fail as an unknown property.
            using var unknownProperty = await GetFeatureResponseAsync(
                fixture, version, usePost, "legacy_table", 5005, "geometry");
            unknownProperty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            async Task AssertGeometryAsync(string typeName, int id, string geometryType,
                double[] expectedCoordinates, string propertyName = "geometry")
            {
                var schema = await DescribeAsync(fixture, version, usePost, typeName);
                schema.Descendants(Xsd + "element").Should().ContainSingle(
                    element => (string?)element.Attribute("name") == propertyName,
                    $"{typeName} must advertise its geometry property");

                var document = await GetFeatureAsync(fixture, version, usePost, typeName, id);
                var feature = document.Descendants(Honua + typeName).Single();
                var geometry = feature.Element(Honua + propertyName)?.Element(Gml + geometryType);
                geometry.Should().NotBeNull($"{version} {typeName} must return the advertised geometry");
                if (geometry is null)
                {
                    return;
                }

                feature.Elements(Honua + propertyName).Should().ContainSingle();
                if (propertyName != "geometry")
                {
                    feature.Elements(Honua + "geometry").Should().BeEmpty();
                }

                var coordinateElement = version == "1.0.0"
                    ? "coordinates"
                    : geometryType == "Point" ? "pos" : "posList";
                var values = geometry.Descendants(Gml + coordinateElement).Single().Value
                    .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                var expected = version == "1.0.0"
                    ? expectedCoordinates
                    : expectedCoordinates.Chunk(2).SelectMany(pair => new[] { pair[1], pair[0] }).ToArray();
                values.Should().Equal(expected, "GML coordinates must preserve version-specific axis order");
                ((string?)geometry.Attribute("srsName")).Should().Be(version == "1.0.0"
                    ? "EPSG:4326" : "urn:ogc:def:crs:EPSG::4326");
            }

            async Task AssertMultiGeometryAsync(string typeName, int id,
                string memberGeometryName, int expectedMemberCount)
            {
                // WFS 1.0 serializes GML 2, which only has the MultiLineString/MultiPolygon
                // spellings; GML 3.0 deprecated those in favour of MultiCurve/MultiSurface,
                // so the WFS 1.1 writer must emit the current GML 3.1 aggregates.
                var (aggregateName, memberName) = (version, memberGeometryName) switch
                {
                    (_, "Point") => ("MultiPoint", "pointMember"),
                    ("1.0.0", "LineString") => ("MultiLineString", "lineStringMember"),
                    ("1.0.0", _) => ("MultiPolygon", "polygonMember"),
                    (_, "LineString") => ("MultiCurve", "curveMember"),
                    _ => ("MultiSurface", "surfaceMember")
                };

                var document = await GetFeatureAsync(fixture, version, usePost, typeName, id);
                var feature = document.Descendants(Honua + typeName).Single();
                var aggregate = feature.Element(Honua + "geometry")?.Element(Gml + aggregateName);
                aggregate.Should().NotBeNull(
                    $"{version} {typeName} must return its declared gml:{aggregateName}");
                if (aggregate is null)
                {
                    return;
                }

                document.Descendants(Gml + "MultiGeometry").Should().BeEmpty(
                    "a homogeneous aggregate must not degrade to a generic gml:MultiGeometry");
                var members = aggregate.Elements(Gml + memberName).ToArray();
                members.Should().HaveCount(expectedMemberCount);
                foreach (var member in members)
                {
                    member.Elements(Gml + memberGeometryName).Should().ContainSingle()
                        .Which.Attribute("srsName").Should().BeNull("only the aggregate carries srsName");
                }

                var coordinateElement = version == "1.0.0"
                    ? "coordinates"
                    : memberGeometryName == "Point" ? "pos" : "posList";
                aggregate.Descendants(Gml + coordinateElement).Should().HaveCount(expectedMemberCount);
                ((string?)aggregate.Attribute("srsName")).Should().Be(version == "1.0.0"
                    ? "EPSG:4326" : "urn:ogc:def:crs:EPSG::4326");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class RecordingFeatureReader(IFeatureReader inner, List<FeatureQuery> queries) : IFeatureReader
    {
        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.GetAsync(layerId, featureId, cancellationToken);

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            queries.Add(query);
            return inner.QueryAsync(layerId, query, cancellationToken);
        }

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryFlatGeobufAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryObjectIdsAsync(layerId, query, cancellationToken);

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.CountAsync(layerId, query, cancellationToken);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => inner.GetExtentAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryStatisticsAsync(layerId, query, cancellationToken);

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
            => inner.GetTemporalExtentAsync(layerId, fieldName, propertyType, cancellationToken);

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => inner.GetEstimatesAsync(layerId, cancellationToken);

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryTopFeaturesAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
            => inner.QueryDateBinsAsync(layerId, query, dateBin, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
            => inner.QueryBinsAsync(layerId, query, binDefinition, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
            => inner.QueryH3Async(layerId, query, h3Query, cancellationToken);
    }

    private static async Task<XDocument> GetFeatureAsync(WebAppFixture fixture, string version,
        bool usePost, string typeName, int id, string? propertyName = null)
        => await ReadAsync(await GetFeatureResponseAsync(fixture, version, usePost, typeName, id, propertyName));

    private static async Task<HttpResponseMessage> GetFeatureResponseAsync(WebAppFixture fixture,
        string version, bool usePost, string typeName, int id, string? propertyName = null)
    {
        if (!usePost)
        {
            return await fixture.Client.GetAsync(
                $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION={version}&TYPENAME={typeName}" +
                $"&FEATUREID={typeName}.{id}&SRSNAME=urn:ogc:def:crs:EPSG::4326" +
                (propertyName is null ? string.Empty : $"&PROPERTYNAME={propertyName}"));
        }

        // WFS 1.0/1.1 POST carries one wfs:PropertyName element per requested property.
        var property = propertyName is null
            ? string.Empty
            : string.Concat(propertyName.Split(',')
                .Select(name => $"<wfs:PropertyName>{name}</wfs:PropertyName>"));
        using var body = new StringContent($"""
            <wfs:GetFeature service="WFS" version="{version}"
                xmlns:wfs="http://www.opengis.net/wfs" xmlns:ogc="http://www.opengis.net/ogc">
              <wfs:Query typeName="{typeName}" srsName="urn:ogc:def:crs:EPSG::4326">
                {property}
                <ogc:Filter><ogc:FeatureId fid="{typeName}.{id}" /></ogc:Filter>
              </wfs:Query>
            </wfs:GetFeature>
            """, Encoding.UTF8, "application/xml");
        return await fixture.Client.PostAsync("/wfs", body);
    }

    private static async Task<XDocument> DescribeAsync(WebAppFixture fixture, string version,
        bool usePost, string typeName)
    {
        if (!usePost)
        {
            return await ReadAsync(await fixture.Client.GetAsync(
                $"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION={version}&TYPENAME={typeName}"));
        }

        using var body = new StringContent($"""
            <wfs:DescribeFeatureType service="WFS" version="{version}" xmlns:wfs="http://www.opengis.net/wfs">
              <wfs:TypeName>{typeName}</wfs:TypeName>
            </wfs:DescribeFeatureType>
            """, Encoding.UTF8, "application/xml");
        return await ReadAsync(await fixture.Client.PostAsync("/wfs", body));
    }

    private static async Task<XDocument> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return XDocument.Parse(body);
        }
    }

    private static async Task SeedGeometriesAsync(WebAppFixture fixture)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (objectid, layer_id, geometry, attributes) VALUES
              (5001, 0, ST_GeomFromText('POINT(-122.5 37.5)', 4326),
                '{"objectid":5001,"name":"Legacy Point"}'::jsonb),
              (5002, 1, ST_GeomFromText('LINESTRING(-122.5 37.5,-122.25 37.75)', 4326),
                '{"objectid":5002,"name":"Legacy Line"}'::jsonb),
              (5003, 2, ST_GeomFromText('POLYGON((-122.5 37.5,-122.25 37.5,-122.25 37.75,-122.5 37.5))', 4326),
                '{"objectid":5003,"name":"Legacy Polygon"}'::jsonb),
              (5004, 3, ST_GeomFromText('POINT(-122.5 37.5)', 4326),
                '{"objectid":5004,"name":"Explicit Geometry"}'::jsonb),
              (5005, 4, ST_GeomFromText('POINT(-122.5 37.5)', 4326),
                '{"objectid":5005,"name":"Nonspatial Resource"}'::jsonb),
              (5006, 5, ST_GeomFromText('MULTIPOINT(-122.5 37.5,-122.25 37.75)', 4326),
                '{"objectid":5006,"name":"Legacy MultiPoint"}'::jsonb),
              (5007, 6, ST_GeomFromText('MULTILINESTRING((-122.5 37.5,-122.25 37.75))', 4326),
                '{"objectid":5007,"name":"Legacy MultiLine"}'::jsonb),
              (5008, 7, ST_GeomFromText(
                'MULTIPOLYGON(((-122.5 37.5,-122.25 37.5,-122.25 37.75,-122.5 37.5)))', 4326),
                '{"objectid":5008,"name":"Legacy MultiPolygon"}'::jsonb);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static TestMetadataV2GraphProvider BuildMetadataProvider()
    {
        var builder = new TestMetadataV2GraphBuilder();
        builder.AddService("legacy-geometry-service", "legacy_geometry",
            accessPolicy: new AccessPolicy { AllowAnonymous = true },
            protocols: [MetadataV2ServiceProtocols.Wfs20]);
        (string Name, MetadataV2GeometryType Type, bool Explicit)[] resources =
        [
            ("legacy_point", MetadataV2GeometryType.Point, false),
            ("legacy_line", MetadataV2GeometryType.LineString, false),
            ("legacy_polygon", MetadataV2GeometryType.Polygon, false),
            ("legacy_explicit", MetadataV2GeometryType.Point, true),
            ("legacy_table", MetadataV2GeometryType.None, false),
            ("legacy_multipoint", MetadataV2GeometryType.MultiPoint, false),
            ("legacy_multiline", MetadataV2GeometryType.MultiLineString, false),
            ("legacy_multipolygon", MetadataV2GeometryType.MultiPolygon, false)
        ];
        foreach (var (name, type, explicitField) in resources)
        {
            // Discovery exposes one WFS type per storage layer, so each control
            // needs its own binding even though all rows use the same table.
            var layerId = Array.FindIndex(resources, resource => resource.Name == name);
            List<MetadataV2Field> fields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = true }
            ];
            if (explicitField)
            {
                fields.Add(new() { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true });
            }

            builder.AddResource(name, name, MetadataV2ResourceType.FeatureDataset,
                    accessPolicy: new AccessPolicy { AllowAnonymous = true }, fields: [.. fields],
                    spatial: new MetadataV2ResourceSpatial
                    {
                        SpatialReference = MetadataV2SpatialReference.Wgs84,
                        GeometryType = type,
                        SupportedCrs = [MetadataV2SpatialReference.Wgs84]
                    })
                .AddStorageBinding($"binding-{name}", name, "features", storageLayerId: layerId,
                    options: new Dictionary<string, JsonElement>
                    {
                        ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
                        ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes")
                    })
                .AddPublication($"publication-{name}", "legacy-geometry-service", name,
                    layerIndex: layerId,
                    storageBindingId: $"binding-{name}", serviceLocalId: name,
                    publicationType: MetadataV2PublicationType.WfsFeatureType);
        }

        return new TestMetadataV2GraphProvider(builder.Build());
    }
}
