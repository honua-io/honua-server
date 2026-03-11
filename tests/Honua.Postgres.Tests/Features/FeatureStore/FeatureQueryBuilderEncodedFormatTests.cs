// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderEncodedFormatTests
{
    [Fact]
    public void BuildSelectGeoJsonQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildSelectGeoJsonQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("ST_AsGeoJSON(");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
    }

    [Fact]
    public void BuildSelectKmlQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildSelectKmlQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("ST_AsKML(");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
    }

    [Fact]
    public void BuildSelectGeobufQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();
        var layer = CreateLayer();

        var result = queryBuilder.BuildSelectGeobufQuery(layer, layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("SELECT ST_AsGeobuf(q, 'geometry') FROM (");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
        result.Sql.Should().NotContain("attributes::text AS attributes");
        result.Sql.Should().Contain("attributes->> $2 AS \"name\"");
        result.Sql.Should().Contain("NULLIF(attributes->> $3, '')::integer AS \"population\"");
        result.Sql.Should().Contain("NULLIF(attributes->> $4, '')::boolean AS \"active\"");
        result.WhereParameters.Should().Equal("name", "population", "active");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
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
