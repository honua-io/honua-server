// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresStorageMappedFeatureReaderP0SqlTests
{
    [UnitTest]
    public void BuildTemporalFilter_NativeTemporalColumn_EmitsIndexFriendlyDirectPredicate()
    {
        var resource = CreateResource(
            [new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.DateTime }]);
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero),
            }
        };

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateReader(resource, attributesColumn: null), [query, false])!.ToString()!;

        sql.Should().Contain("\"observed_at\" >= $");
        sql.Should().Contain("\"observed_at\" <= $");
        sql.Should().NotContain("to_timestamp(");
        sql.Should().NotContain("COALESCE(\"observed_at\", \"observed_at\")");
    }

    [UnitTest]
    public void BuildTemporalFilter_JsonbTemporalColumn_RetainsEpochAwareWrapper()
    {
        var resource = CreateResource(
            [new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.DateTime }]);
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        };

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateReader(resource, attributesColumn: "attributes"), [query, false])!.ToString()!;

        sql.Should().Contain("to_timestamp(");
        sql.Should().Contain("^-?[0-9]+$");
    }

    [UnitTest]
    public void BuildEncodedBinaryAttributeExpression_TemporalFields_UsesEpochAwareCast()
    {
        var resource = CreateResource(
            [
                new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.DateTime },
                new MetadataV2Field { Name = "recorded_on", Type = MetadataV2FieldType.Date },
            ]);
        var reader = CreateReader(resource, attributesColumn: "attributes");

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildEncodedBinarySelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, ["ST_AsGeobuf", false, new FeatureQuery()])!.ToString()!;

        sql.Should().Contain("to_timestamp(");
        sql.Should().Contain(")::date AS \"recorded_on\"");
    }

    [UnitTest]
    public void BuildTileSql_FullPassBelowSimplifyZoom_AppliesSimplification()
    {
        var reader = CreateReader(CreateResource([]), storageSrid: 3857);
        var query = new FeatureQuery { SpatialReferenceSrid = 3857 };
        var tileRequest = new MvtTileRequest
        {
            LayerId = 1,
            X = 0,
            Y = 0,
            Z = 5,
            Query = query,
            TileOptions = new TileOptions { SimplifyZoom = 10, TileBuffer = 64, TileExtent = 4096 },
            TileLimits = new TileLimits { MaxFeaturesPerTile = 0 }
        };
        var spec = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("ResolveTileBuildSpec", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [tileRequest, query])!;
        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildMvtTileSql", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [spec, MvtTileBudgetStep.Full])!.ToString()!;

        sql.Should().Contain("ST_SimplifyPreserveTopology(");
    }

    private static MetadataV2Object CreateResource(MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-p0", Name = "p0_layer" },
            SchemaFields = fields
        };

    private static PostgresStorageMappedFeatureReader CreateReader(
        MetadataV2Object resource,
        string? attributesColumn = null,
        int? storageSrid = null)
        => new(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            new DefaultObjectPoolProvider().Create(
                new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource,
            new FeatureStorageMapping(
                TableName: "secure_features",
                PrimaryKeyColumn: "objectid",
                GeometryColumn: "geom",
                AttributesColumn: attributesColumn,
                StorageSrid: storageSrid),
            connection: null,
            connectionEncryptionService: null);
}
