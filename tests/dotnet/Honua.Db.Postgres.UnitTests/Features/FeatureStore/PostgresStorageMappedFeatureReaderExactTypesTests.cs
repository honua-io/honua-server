// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresStorageMappedFeatureReaderExactTypesTests
{
    [UnitTest]
    public void BuildFeatureSelect_DecimalColumn_ProjectsCanonicalText()
    {
        var sql = BuildFeatureSelect(attributesColumn: null);

        sql.Should().Contain("\"amount\"::text");
        sql.Should().NotContain("\"observed_at\"::text");
        sql.Should().NotContain("\"ratio\"::text");
    }

    [UnitTest]
    public void BuildFeatureSelect_DecimalInAttributesJson_UsesTextAccessor()
    {
        var sql = BuildFeatureSelect(attributesColumn: "attributes");

        sql.Should().Contain("\"attributes\" ->> ");
        sql.Should().NotContain("\"amount\"::text");
    }

    [UnitTest]
    public void BuildFeatureSelect_JsonbDecimalTimestampOffsetAndUuid_EmitsTypedSortAndWhereCasts()
    {
        var query = new FeatureQuery
        {
            Where = "amount > 10",
            OrderBy =
            [
                OrderByClause.Asc("amount"),
                OrderByClause.Desc("observed_at"),
                OrderByClause.Asc("external_id"),
            ]
        };

        var sql = BuildFeatureSelect(attributesColumn: "attributes", query);

        sql.Should().Contain("::numeric");
        sql.Should().Contain("::timestamptz");
        sql.Should().Contain("::uuid");
    }

    private static string BuildFeatureSelect(string? attributesColumn, FeatureQuery? query = null)
    {
        var reader = new PostgresStorageMappedFeatureReader(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            CreateResource(),
            new FeatureStorageMapping(
                TableName: "exact_features",
                PrimaryKeyColumn: "objectid",
                GeometryColumn: "geom",
                AttributesColumn: attributesColumn),
            connection: null,
            connectionEncryptionService: null);

        return typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query ?? new FeatureQuery(), false])!
            .ToString()!;
    }

    private static MetadataV2Object CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-exact", Name = "Exact" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "amount", Type = MetadataV2FieldType.Decimal },
                new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.TimestampOffset },
                new MetadataV2Field { Name = "ratio", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "external_id", Type = MetadataV2FieldType.Uuid },
            ],
        };
}
