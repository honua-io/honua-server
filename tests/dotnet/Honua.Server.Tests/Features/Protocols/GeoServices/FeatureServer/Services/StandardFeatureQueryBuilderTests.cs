// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services.QueryBuilding;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class StandardFeatureQueryBuilderTests
{
    [Fact]
    public void BuildQuery_WhenSpatialRelationshipIsInvalid_ThrowsSanitizedInvalidOperationException()
    {
        var sut = new StandardFeatureQueryBuilder();
        var context = new QueryBuildingContext
        {
            QueryParams = new QueryParameters
            {
                SpatialRel = "not-a-real-relationship"
            },
            Service = CreateService(),
            Layer = CreateLayer(),
            ParsedGeometry = new GeoServicesGeometry { X = 1, Y = 2 },
            InputSrid = 4326
        };

        var act = () => sut.BuildQuery(context);

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Be("Invalid spatial parameters.");
        thrown.InnerException.Should().NotBeNull();
    }

    [Fact]
    public void BuildQuery_WhenPointLayerIntersectsEnvelope_UsesEnvelopeIntersects()
    {
        var sut = new StandardFeatureQueryBuilder();
        var context = new QueryBuildingContext
        {
            QueryParams = new QueryParameters
            {
                GeometryType = "esriGeometryEnvelope",
                SpatialRel = "esriSpatialRelIntersects"
            },
            Service = CreateService(),
            Layer = CreateLayer(),
            ParsedGeometry = new GeoServicesGeometry
            {
                Xmin = 1,
                Ymin = 2,
                Xmax = 3,
                Ymax = 4
            },
            InputSrid = 4326
        };

        var query = sut.BuildQuery(context);

        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.SpatialRelationship.Should().Be(SpatialRelationship.EnvelopeIntersects);
    }

    private static ServiceDefinition CreateService()
    {
        var layer = CreateLayer();
        return ServiceDefinition.CreateSingle("test", layer, SpatialReference.WGS84);
    }

    private static LayerDefinition CreateLayer()
        => new(
            1,
            "test-layer",
            null,
            GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: false)
            ]);
}
