// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerOptionalProjectionTests : MapServerEndpointTestBase
{
    public MapServerOptionalProjectionTests()
    {
        var store = Substitute.For<IRelationshipStore>();
        var geometry = new WKBWriter().Write(new NetTopologySuite.Geometries.Point(-122.5, 37.5));
        var feature = Feature.Create(501, geometry, ImmutableDictionary<string, object?>.Empty
            .Add("objectid", 501).Add("related_id", 1));
        store.QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, [feature])));
        Fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IGeometryOperationService>();
            services.AddSingleton(store);
        });
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task RelatedQuery_WithoutProjector_PreservesSourceCoordinatesAndAttributeOnlyRequests()
    {
        foreach (var (protocol, extra, hasGeometry) in new[]
        {
            ("FeatureServer", "&returnGeometry=true", true),
            ("MapServer", "&returnGeometry=true&outSR=4326", true),
            ("MapServer", "&returnGeometry=false", false)
        })
        {
            using var response = await Fixture.Client.GetAsync(RelatedUrl(protocol, extra));
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse(content);
            var records = document.RootElement.GetProperty("relatedRecordGroups")[0].GetProperty("relatedRecords");
            records.GetArrayLength().Should().Be(1);
            records[0].GetProperty("attributes").GetProperty("objectid").GetInt64().Should().Be(501);
            if (hasGeometry)
            {
                records[0].GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-122.5);
                records[0].GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(37.5);
            }
            else
            {
                records[0].TryGetProperty("geometry", out _).Should().BeFalse();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords")]
    public async Task RelatedQuery_WithoutProjector_RejectsRequiredCoordinateTransformation()
    {
        using var response = await Fixture.Client.GetAsync(RelatedUrl("MapServer", "&returnGeometry=true"));
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("message").GetString().Should().Contain("reprojection");
        document.RootElement.TryGetProperty("relatedRecordGroups", out _).Should().BeFalse();
    }

    private static string RelatedUrl(string protocol, string extra)
        => $"/rest/services/{WebAppFixture.TestServiceId}/{protocol}/{WebAppFixture.TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId=1{extra}";
}
