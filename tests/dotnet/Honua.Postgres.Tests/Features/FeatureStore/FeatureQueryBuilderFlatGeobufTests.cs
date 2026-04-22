// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderFlatGeobufTests
{
    [Fact]
    public void BuildSelectFlatGeobufQuery_UsesPostGisEncoder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
        var layer = CreateLayer();

        var result = queryBuilder.BuildSelectFlatGeobufQuery(layer, layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("SELECT ST_AsFlatGeobuf(q, true, 'geometry') FROM (");
        result.Sql.Should().NotContain("ST_AsBinary(");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("attributes::text AS attributes");
        result.Sql.Should().Contain("attributes->> $2 AS \"name\"");
        result.Sql.Should().Contain("NULLIF(attributes->> $3, '')::integer AS \"population\"");
        result.Sql.Should().Contain("NULLIF(attributes->> $4, '')::boolean AS \"active\"");
        result.WhereParameters.Should().Equal("name", "population", "active");
    }

    [Fact]
    public void BuildSelectFlatGeobufQuery_WithOutFields_ProjectsRequestedAttributes()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
        var layer = CreateLayer();
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("active", "population")
        };

        var result = queryBuilder.BuildSelectFlatGeobufQuery(layer, layerId: 1, query: query);

        result.Sql.Should().Contain("NULLIF(attributes->> $2, '')::boolean AS \"active\"");
        result.Sql.Should().Contain("NULLIF(attributes->> $3, '')::integer AS \"population\"");
        result.Sql.Should().NotContain(" AS \"name\"");
        result.WhereParameters.Should().Equal("active", "population");
    }

    [Fact]
    public void BuildSelectFlatGeobufQuery_WithKnnFilter_AppliesNearestNeighborOrderingAndLimit()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
        var layer = CreateLayer();
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5, srid: 4326)
        };

        var result = queryBuilder.BuildSelectFlatGeobufQuery(layer, layerId: 1, query: query);

        result.Sql.Should().Contain("ORDER BY ST_Distance(");
        result.Sql.Should().Contain("::geography");
        result.Sql.Should().Contain(" LIMIT $");
    }

    private static LayerDefinition CreateLayer()
    {
        return new LayerDefinition(
            Id: 1,
            Name: "Test Layer",
            Description: null,
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.Create(4326),
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true),
                new FieldDefinition("name", FieldType.String, Length: 255),
                new FieldDefinition("population", FieldType.Integer),
                new FieldDefinition("active", FieldType.Boolean)
            ]);
    }
}
