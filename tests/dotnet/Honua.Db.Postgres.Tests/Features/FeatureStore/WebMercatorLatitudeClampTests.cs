// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Proves feature-query call sites pass the source SRID into <see cref="DatumTransformSql"/>
/// so a geographic layer or filter reprojected to Web Mercator is clipped first.
/// </summary>
public sealed class WebMercatorLatitudeClampTests
{
    [UnitTheory]
    [InlineData("select")]
    [InlineData("gml")]
    [InlineData("geojson")]
    [InlineData("kml")]
    [InlineData("extent")]
    [InlineData("geobuf")]
    public void OutputReprojection_GeographicToWebMercator_ClampsBeforeTransform(string format)
    {
        var sql = Build(format, new FeatureQuery { SpatialReferenceSrid = 4326, OutputSrid = 102100 });

        AssertClipPrecedesTransform(sql, sourceSrid: 4326, targetSrid: 102100);
    }

    [UnitTest]
    public void OutputReprojection_GeographicToNonMercator_DoesNotClamp()
    {
        var sql = Build("select", new FeatureQuery { SpatialReferenceSrid = 4326, OutputSrid = 4269 });

        sql.Should().Contain("ST_Transform(geometry, 4269)");
        sql.Should().NotContain("ST_ClipByBox2D");
        sql.Should().NotContain("85.0511287798066");
    }

    [UnitTest]
    public void OutputReprojection_ProjectedToWebMercator_DoesNotClamp()
    {
        var sql = Build("select", new FeatureQuery { SpatialReferenceSrid = 26911, OutputSrid = 3857 });

        sql.Should().Contain("ST_Transform(geometry, 3857)");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    [UnitTest]
    public void SpatialFilter_GeographicIntoWebMercatorLayer_ClampsBeforeTransform()
    {
        var sql = Build("select", new FeatureQuery
        {
            SpatialReferenceSrid = 3857,
            SpatialFilter = SpatialFilter.Create([1, 2, 3], SpatialRelationship.Intersects, srid: 4326)
        });

        AssertClipPrecedesTransform(sql, sourceSrid: 4326, targetSrid: 3857);
    }

    [UnitTest]
    public void SpatialFilter_GeographicIntoNonMercatorLayer_DoesNotClamp()
    {
        var sql = Build("select", new FeatureQuery
        {
            SpatialReferenceSrid = 26911,
            SpatialFilter = SpatialFilter.Create([1, 2, 3], SpatialRelationship.Intersects, srid: 4326)
        });

        sql.Should().Contain("ST_Transform(");
        sql.Should().Contain("26911");
        sql.Should().NotContain("ST_ClipByBox2D");
    }

    private static string Build(string format, FeatureQuery query)
    {
        var builder = CreateQueryBuilder();
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = [],
        };

        return format switch
        {
            "select" => builder.BuildSelectQuery(1, query).Sql,
            "gml" => builder.BuildSelectGmlQuery(1, query).Sql,
            "geojson" => builder.BuildSelectGeoJsonQuery(1, query).Sql,
            "kml" => builder.BuildSelectKmlQuery(1, query).Sql,
            "extent" => builder.BuildExtentQuery(1, query).Sql,
            "geobuf" => builder.BuildSelectGeobufQuery(resource, 1, query).Sql,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var pool = new DefaultObjectPoolProvider().Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor());
    }

    private static void AssertClipPrecedesTransform(string sql, int sourceSrid, int targetSrid)
    {
        var envelope = $"ST_MakeEnvelope(-180, -85.0511287798066, 180, 85.0511287798066, {sourceSrid})";
        sql.Should().Contain($"ST_Transform(ST_ClipByBox2D(");
        sql.Should().Contain(envelope);
        sql.Should().Contain($", {targetSrid.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }
}
