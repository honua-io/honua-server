// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// A WMS FILTER geometry with no srsName is read in the layer CRS. The stand-in reader
/// returns the known feature only when the translated geometry parameter is EPSG:3857,
/// so a 4326 default fails the assertion (#6184).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsFilterCrsTests : IAsyncLifetime
{
    private const string HitName = "mercator-hit";

    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();
    private readonly WebAppFixture _fixture = new();
    private FeatureQuery? _filteredQuery;

    public async Task InitializeAsync()
    {
        _reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<FeatureQuery>();
                if (query.SqlFilter is not { } filter)
                {
                    return Empty();
                }

                _filteredQuery = query;
                return FilterUsesLayerSrid(filter) ? Hit() : Empty();
            });
        _fixture.ReplaceRequestService(_reader);
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            spatial: new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                StorageCrs = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.WebMercator, MetadataV2SpatialReference.Wgs84]
            },
            clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_FilterGeometryWithoutSrsName_UsesLayerCrs()
    {
        const string filter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">" +
            "<fes:BBOX><fes:ValueReference>shape</fes:ValueReference>" +
            "<gml:Envelope><gml:lowerCorner>-1000 -1000</gml:lowerCorner>" +
            "<gml:upperCorner>1000 1000</gml:upperCorner></gml:Envelope></fes:BBOX></fes:Filter>";

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0" +
            "&CRS=EPSG:3857&BBOX=-1000,-1000,1000,1000&WIDTH=10&HEIGHT=10" +
            $"&LAYERS={WebAppFixture.TestLayerId}&QUERY_LAYERS={WebAppFixture.TestLayerId}" +
            $"&INFO_FORMAT=application/json&I=5&J=5&FILTER={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain(HitName);
        _filteredQuery.Should().NotBeNull();
        // FeatureQuery is a struct, so FeatureQuery? does not lift SqlFilter.
        // WMS FILTER stores the translated predicate on SqlFilter; its SRID
        // parameters are the #6184 layer-CRS assertion.
        FilterUsesLayerSrid(_filteredQuery.Value.SqlFilter!).Should().BeTrue(content);
    }

    private static bool FilterUsesLayerSrid(SqlFragment filter)
    {
        var srids = filter.Parameters.OfType<int>().ToArray();
        return srids.Contains(MetadataV2SpatialReference.WebMercator.Srid!.Value)
            && !srids.Contains(MetadataV2SpatialReference.Wgs84.Srid!.Value);
    }

    private static QueryResult<Feature> Hit()
        => QueryResult<Feature>.Create(1, [
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", HitName))
        ]);

    private static QueryResult<Feature> Empty()
        => QueryResult<Feature>.Create(0, []);
}
