// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Api.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
public sealed class OgcTilesServiceSelectionTests
{
    private const string OgcApiTilesProtocol = "OGC-API-Tiles";

    [UnitTest]
    public void BuildPrimaryServiceMap_WithPreferredProtocol_SelectsProtocolEnabledService()
    {
        var layer = LayerDefinition.CreateBasic(10, "tiles-layer", CatalogGeometryType.Point);
        var alpha = CreateService(
            "alpha-service",
            layer,
            enabledProtocols: ServiceProtocols.All
                .Where(protocol => !string.Equals(protocol, OgcApiTilesProtocol, StringComparison.Ordinal))
                .ToArray());
        var beta = CreateService("beta-service", layer, enabledProtocols: [OgcApiTilesProtocol]);

        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap([alpha, beta], OgcApiTilesProtocol);

        primaryServices.Should().ContainKey(10);
        primaryServices[10].Name.Should().Be("beta-service");
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

    private static ServiceDefinition CreateService(
        string serviceName,
        LayerDefinition layer,
        string[] enabledProtocols)
        => ServiceDefinition.CreateSingle(
            serviceName,
            layer,
            SpatialReference.Create(layer.SpatialReference.Wkid)) with
        {
            Metadata = new CatalogMetadata
            {
                EnabledProtocols = enabledProtocols
            }
        };
}
