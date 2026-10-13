// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
public sealed class OgcTilesServiceSelectionTests
{
    private const string OgcApiTilesProtocol = "OGC-API-Tiles";

    [UnitTest]
    public void BuildPrimaryServiceMapV2_WithPreferredProtocol_SelectsProtocolEnabledService()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                "svc-alpha",
                "alpha-service",
                protocols: ServiceProtocols.All
                    .Where(protocol => !string.Equals(protocol, OgcApiTilesProtocol, StringComparison.Ordinal))
                    .ToArray())
            .AddService("svc-beta", "beta-service", protocols: [OgcApiTilesProtocol])
            .AddResource("res-tiles", "tiles-layer")
            .AddPublication("pub-alpha", "svc-alpha", "res-tiles", layerIndex: 10)
            .AddPublication("pub-beta", "svc-beta", "res-tiles", layerIndex: 10)
            .Build();
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UtcNow);

        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMapV2(snapshot, OgcApiTilesProtocol);

        primaryServices.Should().ContainKey(10);
        primaryServices[10].Metadata.Name.Should().Be("beta-service");
    }

    [UnitTest]
    public void CreateBboxSpatialFilter_WithAntimeridianBounds_ReturnsMultiPolygonFilter()
    {
        var method = typeof(TilesEndpoints).GetMethod(
            "CreateBboxSpatialFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var bounds = new TileBounds(170.0, -10.0, -170.0, 10.0);
        var result = method!.Invoke(null, [bounds, 4326]);

        result.Should().NotBeNull();
        var spatialFilter = result.Should().BeOfType<SpatialFilter>().Subject;
        spatialFilter.Srid.Should().Be(4326);

        var geometry = new WKBReader().Read(spatialFilter.Geometry);
        geometry.Should().BeOfType<MultiPolygon>();
    }

}
