// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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
        var resource = CreateResource();

        var result = queryBuilder.BuildSelectFlatGeobufQuery(resource, layerId: 1, query: new FeatureQuery());

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
        var resource = CreateResource();
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("active", "population")
        };

        var result = queryBuilder.BuildSelectFlatGeobufQuery(resource, layerId: 1, query: query);

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
        var resource = CreateResource();
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5, srid: 4326)
        };

        var result = queryBuilder.BuildSelectFlatGeobufQuery(resource, layerId: 1, query: query);

        result.Sql.Should().Contain("ORDER BY ST_Distance(");
        result.Sql.Should().Contain("::geography");
        result.Sql.Should().Contain(" LIMIT $");
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test-layer", Name = "Test Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
            },
            SchemaFields =
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, semanticRoles: ["id.primary"]),
                Field("shape", MetadataV2FieldType.Geometry, nullable: true, semanticRoles: ["geometry.primary"]),
                Field("name", MetadataV2FieldType.String),
                Field("population", MetadataV2FieldType.Integer),
                Field("active", MetadataV2FieldType.Boolean),
            ],
        };

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable = true,
        params string[] semanticRoles)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            SemanticRoles = semanticRoles,
        };
}
