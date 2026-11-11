// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("gid")]
    public async Task QueryFlatGeobufAsync_NoSourceObjectId_KeepsHistoricalObjectIdColumn(string primaryKey)
    {
        await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities RENAME COLUMN objectid TO {primaryKey};");
        var reader = CreateReader(primaryKeyColumn: primaryKey);
        var payload = await reader.QueryFlatGeobufAsync(1, new FeatureQuery
        {
            ObjectIds = [1],
            OutFields = [primaryKey, "name"]
        }, CancellationToken.None);
        var encoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload!).Should().ContainSingle().Subject;
        encoded.Attributes["objectid"].Should().Be(1L);
        encoded.Attributes[primaryKey].Should().Be(1L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryFlatGeobufAsync_SourceObjectId_PreservesBusinessValueAndGeometry(bool useAttributes)
    {
        var reader = await CreateSourceIdentityReaderAsync(useAttributes);

        var payload = await reader.QueryFlatGeobufAsync(1, new FeatureQuery
        {
            ObjectIds = [1],
            OutFields = ["id", "objectid", "name"]
        }, CancellationToken.None);

        payload.Should().NotBeNullOrEmpty();
        var feature = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload!).Should().ContainSingle().Subject;
        feature.Attributes["id"].Should().Be(1L);
        feature.Attributes["objectid"].Should().Be(1899L);
        feature.Attributes["name"].Should().Be("Honolulu");
        feature.Geometry.Coordinate.X.Should().BeApproximately(-157.8583, 0.000001);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QueryPageAsync_SourceObjectId_PreservesBusinessValue(bool useAttributes, bool distinct)
    {
        var reader = await CreateSourceIdentityReaderAsync(useAttributes);

        var result = await reader.QueryPageAsync(1, new FeatureQuery
        {
            OutFields = ["objectid"],
            Distinct = distinct,
            Limit = 1,
            SqlFilter = new SqlFragment("\"objectid\" = @p0", [1899L])
        });

        var feature = result.Items.Should().ContainSingle().Subject;
        feature.Attributes["objectid"].Should().Be(1899L);
        if (!distinct)
        {
            feature.Id.Should().Be(1);
            var byCanonicalId = await reader.QueryPageAsync(1, new FeatureQuery
            {
                ObjectIds = [1],
                OutFields = ["id", "objectid", "name"]
            });
            var canonicalFeature = byCanonicalId.Items.Should().ContainSingle().Subject;
            canonicalFeature.Id.Should().Be(1);
            canonicalFeature.Attributes["id"].Should().Be(1L);
            canonicalFeature.Attributes["objectid"].Should().Be(1899L);
            canonicalFeature.Attributes["name"].Should().Be("Honolulu");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryGeoServicesRawPointPageAsync_SourceObjectId_PreservesPublicId(bool useAttributes)
    {
        var reader = await CreateSourceIdentityReaderAsync(useAttributes);
        var raw = reader.Should().BeAssignableTo<IPagedRawGeoServicesFeatureStore>().Subject;
        var page = await raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery
        {
            ObjectIds = [1],
            OutFields = ["objectid"],
            PublicIdAttributeName = "objectid",
            Limit = 1
        });
        var feature = page.Items.Should().ContainSingle().Subject;
        feature.Id.Should().Be(1);
        feature.PublicIdJson.Should().Be("1899");
        using var attributes = JsonDocument.Parse(feature.AttributesJson!);
        attributes.RootElement.GetProperty("objectid").GetInt64().Should().Be(1899);
        feature.X.Should().BeApproximately(-157.8583, 0.000001);
    }

    [Theory]
    [InlineData(false, "id", "objectid")]
    [InlineData(true, "id", "objectid")]
    [InlineData(false, "objectid", "id")]
    [InlineData(true, "objectid", "id")]
    public async Task QueryPageAsync_LegacyMetadata_UsesBindingKeyBeforeSchemaOrder(
        bool useAttributes, string primaryKey, string sourceId)
    {
        if (primaryKey == "id")
        {
            await _fixture.ExecuteAsync($"ALTER TABLE {_schema}.cities RENAME COLUMN objectid TO id;");
        }
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN {sourceId} bigint;
            UPDATE {_schema}.cities SET {sourceId} = 1899, attributes = jsonb_build_object('{sourceId}', 1899);
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null,
            primaryKeyColumn: primaryKey, legacyPrimaryIdLast: true,
            additionalFields: [new() { Name = sourceId, Type = MetadataV2FieldType.BigInteger }]);
        var query = new FeatureQuery { ObjectIds = [1], OutFields = [primaryKey, sourceId] };
        var feature = (await reader.QueryPageAsync(1, query)).Items.Should().ContainSingle().Subject;
        feature.Id.Should().Be(1);
        feature.Attributes[sourceId].Should().Be(1899L);
        var filtered = await reader.QueryPageAsync(1, query with
        {
            SqlFilter = new SqlFragment($"\"{sourceId}\" = @p0", [1899L])
        });
        filtered.Items.Should().ContainSingle();
        var payload = await reader.QueryFlatGeobufAsync(1, query, CancellationToken.None);
        var encoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload!).Should().ContainSingle().Subject;
        encoded.Attributes[sourceId].Should().Be(1899L);
        encoded.Attributes[primaryKey].Should().Be(1L);
    }

    [Theory]
    [InlineData("IN (@p0)")]
    [InlineData("= @p0")]
    [InlineData(">= @p0")]
    public async Task QueryPageAsync_JsonNumericSqlFilter_AcceptsTextJoinKey(string predicate)
    {
        await _fixture.ExecuteAsync($"UPDATE {_schema}.cities SET attributes = jsonb_build_object('population', population, 'name', name);");
        var result = await CreateReader(attributesColumn: "attributes").QueryPageAsync(1, new FeatureQuery
        {
            SqlFilter = new SqlFragment($"\"population\" {predicate} AND \"name\" = @p1", ["350000", "Honolulu"])
        });
        result.Items.Select(feature => feature.Id).Should().Equal(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPageAsync_SemanticPublicId_DoesNotAliasStorageKey(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN public_id text;
            UPDATE {_schema}.cities SET public_id = 'public-' || objectid,
                attributes = jsonb_build_object('public_id', 'public-' || objectid);
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null,
            semanticPrimaryId: true,
            additionalFields: [new() { Name = "public_id", Type = MetadataV2FieldType.String, SemanticRoles = ["id.primary"] }]);
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"public_id\" = @p0", ["public-1"]),
            OutFields = ["objectid", "public_id"]
        };
        var feature = (await reader.QueryPageAsync(1, query)).Items.Should().ContainSingle().Subject;
        feature.Id.Should().Be(1);
        feature.Attributes["public_id"].Should().Be("public-1");
        var payload = await reader.QueryFlatGeobufAsync(1, query, CancellationToken.None);
        var encoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload!).Should().ContainSingle().Subject;
        encoded.Attributes["objectid"].Should().Be(1L);
        encoded.Attributes["public_id"].Should().Be("public-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryObjectIdsAsync_LegacySourceObjectId_ReturnsPublicIds(bool useAttributes)
    {
        var reader = await CreateSourceIdentityReaderAsync(useAttributes);
        var query = new FeatureQuery
        {
            PublicIdAttributeName = "objectid",
            SqlFilter = new SqlFragment("\"objectid\" = @p0", [1899L])
        };
        var storageIds = await reader.QueryObjectIdsAsync(1, query);
        storageIds.Should().Equal(1L);
        var publicIds = await reader.QueryObjectIdsAsync(1, query with { ReturnPublicObjectIds = true });
        publicIds.Should().Equal(1899L);
    }

    private async Task<PostgresStorageMappedFeatureReader> CreateSourceIdentityReaderAsync(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities RENAME COLUMN objectid TO id;
            ALTER TABLE {_schema}.cities ADD COLUMN objectid bigint;
            UPDATE {_schema}.cities SET objectid = CASE id WHEN 1 THEN 1899 ELSE 2848 END;
            UPDATE {_schema}.cities SET attributes = jsonb_build_object('objectid', objectid, 'name', name, 'population', population);
            """);
        return CreateReader(
            attributesColumn: useAttributes ? "attributes" : null,
            primaryKeyColumn: "id",
            additionalFields: [new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger }]);
    }
}
