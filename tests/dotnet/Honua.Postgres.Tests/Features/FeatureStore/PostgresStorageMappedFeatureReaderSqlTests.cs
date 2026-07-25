// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresStorageMappedFeatureReaderSqlTests
{
    [Fact]
    public async Task ApplyReadSecurityAsync_WithRlsAndFieldMask_EnforcesBothOnTileQuery()
    {
        var resource = CreateResource();
        var rlsSource = Substitute.For<IRowLevelSecurityFilterSource>();
        rlsSource.ResolveAsync(resource, Arg.Any<CancellationToken>())
            .Returns(new SqlFragment("\"tenant_id\" = @p0", ["tenant-a"]));
        var fieldMaskSource = Substitute.For<IFieldMaskSource>();
        fieldMaskSource.ResolveAsync(resource, Arg.Any<CancellationToken>())
            .Returns(["secret"]);
        var reader = CreateReader(resource, rlsSource, fieldMaskSource);
        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "ApplyReadSecurityAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull();
        var task = (Task<FeatureQuery>)method!.Invoke(
            reader,
            [new FeatureQuery(), CancellationToken.None])!;
        var securedQuery = await task;

        securedQuery.EnforcedSqlFilter.Should().NotBeNull();
        securedQuery.EnforcedSqlFilter!.Sql.Should().Be("\"tenant_id\" = @p0");
        securedQuery.EnforcedSqlFilter.Parameters.Should().Equal("tenant-a");
        securedQuery.EnforcedMaskedFields.Should().ContainSingle().Which.Should().Be("secret");
    }

    [Fact]
    public void ResolveAttributeFields_WithEnforcedMask_DropsMaskedTileAttribute()
    {
        var resource = CreateResource();
        var reader = CreateReader(resource);
        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "ResolveAttributeFields",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull();
        var fields = (MetadataV2Field[])method!.Invoke(
            reader,
            [new FeatureQuery { EnforcedMaskedFields = ["secret"] }])!;

        fields.Select(static field => field.Name).Should().Equal("tenant_id", "name");
    }

    [Fact]
    public void RewriteAttributeTextAccessExpressions_WithCanonicalAttributeFilter_UsesMappedSourceColumn()
    {
        var rewritten = PostgresStorageMappedFeatureReader.RewriteAttributeTextAccessExpressions(
            "\"attributes\" ->> 'dam_name' IN ($1, $2)",
            field =>
            {
                field.Should().Be("dam_name");
                return "\"dam_name\"";
            });

        rewritten.Should().Be("NULLIF((\"dam_name\")::text, '') IN ($1, $2)");
    }

    [Fact]
    public void RewriteAttributeTextAccessExpressions_WithStringField_PreservesEmptyStringForIsNotNull()
    {
        // Esri/PostgreSQL treat an empty string as a non-NULL value. The storage-mapped
        // reader must not wrap a text/string column in NULLIF(..., '') when translating an
        // IS NOT NULL filter, or empty-string rows would be silently dropped (#1703).
        var rewritten = PostgresStorageMappedFeatureReader.RewriteAttributeTextAccessExpressions(
            "\"attributes\" ->> 'uniquedesignation' IS NOT NULL",
            field =>
            {
                field.Should().Be("uniquedesignation");
                return "\"uniquedesignation\"";
            },
            _ => MetadataV2FieldType.String);

        rewritten.Should().Be("(\"uniquedesignation\")::text IS NOT NULL");
    }

    [Fact]
    public void RewriteAttributeTextAccessExpressions_WithNumericField_KeepsEmptyStringCoercion()
    {
        // Numeric/temporal fields keep NULLIF(..., '') so empty text does not break a
        // downstream ::numeric/::timestamptz cast and Esri's "empty numeric == null"
        // semantics still hold.
        var rewritten = PostgresStorageMappedFeatureReader.RewriteAttributeTextAccessExpressions(
            "\"attributes\" ->> 'shape__length' IS NOT NULL",
            _ => "\"shape__length\"",
            _ => MetadataV2FieldType.Double);

        rewritten.Should().Be("NULLIF((\"shape__length\")::text, '') IS NOT NULL");
    }

    [Fact]
    public void BuildDistancePredicateSql_WithProjectedStorage_ReprojectsBothOperandsToWgs84Geography()
    {
        // Projected-storage layer (EPSG:3857). A bare ::geography cast requires lon/lat and would
        // throw at execution (500); the predicate must reproject both operands to WGS84 geography
        // via ST_Transform(...,4326)::geography so ST_Distance returns metres (#2740).
        // Seed the filter-geometry parameter ($1) so the distance parameter realistically becomes $2.
        var parameters = new List<object?> { new byte[] { 1, 2, 3, 4 } };

        var predicate = PostgresStorageMappedFeatureReader.BuildDistancePredicateSql(
            geometryColumn: "\"geom\"::geometry",
            filterGeometry: "$1",
            storageSrid: 3857,
            distanceInMeters: 1000d,
            beyond: false,
            addParameter: value =>
            {
                parameters.Add(value);
                return "$" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });

        predicate.Should().Contain("ST_Transform(\"geom\"::geometry, 4326)::geography");
        predicate.Should().Contain("ST_Transform($1, 4326)::geography");
        predicate.Should().StartWith("ST_Distance(");
        predicate.Should().Contain("<= $2");
        parameters.Last().Should().Be(1000d);
    }

    [Fact]
    public void BuildDistancePredicateSql_WithWgs84Storage_CastsDirectlyToGeography()
    {
        // Geographic (EPSG:4326) storage is already lon/lat, so no ST_Transform is required —
        // a direct ::geography cast is correct and cheaper.
        var parameters = new List<object?>();

        var predicate = PostgresStorageMappedFeatureReader.BuildDistancePredicateSql(
            geometryColumn: "\"geom\"::geometry",
            filterGeometry: "$1",
            storageSrid: 4326,
            distanceInMeters: 500d,
            beyond: true,
            addParameter: value =>
            {
                parameters.Add(value);
                return "$" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });

        predicate.Should().Contain("\"geom\"::geometry::geography");
        predicate.Should().NotContain("ST_Transform");
        predicate.Should().Contain("> $1");
    }

    [Fact]
    public void BuildStatisticsAggregateExpression_WithNumericAggregate_CastsMappedSourceColumn()
    {
        var expression = PostgresStorageMappedFeatureReader.BuildStatisticsAggregateExpression(
            StatisticType.Avg,
            "\"longitude\"",
            Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldType.Double);

        expression.Should().Be("AVG(NULLIF((\"longitude\")::text, '')::numeric)");
    }

    [Fact]
    public void BuildAttributesExpressionText_WithWideOutFields_ChunksJsonbBuildObjectCalls()
    {
        var fields = Enumerable.Range(1, 51)
            .Select(index => new MetadataV2Field { Name = $"field_{index}", Type = MetadataV2FieldType.String })
            .ToArray();

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "BuildAttributesExpressionText",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(MetadataV2Field[])],
            modifiers: null);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [fields])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().StartWith("(");
        expression.Should().EndWith(")::text");
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', \"field_1\"");
        expression.Should().Contain("'field_51', \"field_51\"");
    }

    [Fact]
    public void BuildAttributesExpressionText_WithJsonbColumn_PreservesNumericTypeButStringifiesText()
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = "site_name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "sync_version", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "elevation", Type = MetadataV2FieldType.Double },
            new MetadataV2Field { Name = "serviceable", Type = MetadataV2FieldType.Boolean },
            new MetadataV2Field { Name = "inspected", Type = MetadataV2FieldType.Date },
        };

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "BuildAttributesExpressionText",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(MetadataV2Field[]), typeof(string)],
            modifiers: null);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [fields, "attributes"])!;

        // Numeric/boolean fields use the jsonb-preserving accessor (->) so they
        // round-trip as JSON numbers/booleans, not strings.
        expression.Should().Contain("'sync_version', \"attributes\" -> 'sync_version'");
        expression.Should().Contain("'elevation', \"attributes\" -> 'elevation'");
        expression.Should().Contain("'serviceable', \"attributes\" -> 'serviceable'");

        // Text/date fields keep the text accessor (->>) — formatting unchanged.
        expression.Should().Contain("'site_name', \"attributes\" ->> 'site_name'");
        expression.Should().Contain("'inspected', \"attributes\" ->> 'inspected'");
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-secure", Name = "Secure" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "tenant_id", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String },
            ],
        };

    private static PostgresStorageMappedFeatureReader CreateReader(
        MetadataV2Resource resource,
        IRowLevelSecurityFilterSource? rlsSource = null,
        IFieldMaskSource? fieldMaskSource = null)
        => new(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            new DefaultObjectPoolProvider().Create(
                new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource,
            new FeatureStorageMapping(
                TableName: "secure_features",
                PrimaryKeyColumn: "objectid",
                GeometryColumn: "geom"),
            connection: null,
            connectionEncryptionService: null,
            rlsFilterSource: rlsSource,
            fieldMaskSource: fieldMaskSource);
}
