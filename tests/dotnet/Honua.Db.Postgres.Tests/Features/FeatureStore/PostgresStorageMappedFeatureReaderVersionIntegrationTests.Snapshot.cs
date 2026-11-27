// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionIntegrationTests
{
    [Fact]
    public async Task GetAsync_ManagedSnapshotTokenIncludesUnprojectedGeometryAndAttributes()
    {
        var full = await CreateReader().GetAsync(1, 1);
        var reader = CreatePartitionReader(new FeatureStorageMapping("features", SchemaName: _schema, GeometryColumn: null,
            AttributesColumn: "attributes", LayerDiscriminatorColumn: "layer_id", LayerDiscriminatorValue: 1), _schema);

        var projected = await reader.GetAsync(1, 1);

        var actual = Assert.IsType<Feature>(projected);
        actual.Geometry.Should().BeNull();
        actual.Attributes.Keys.Should().Equal("objectid");
        actual.ReadStateToken.Should().NotBeNullOrEmpty().And.Be(Assert.IsType<Feature>(full).ReadStateToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_ManagedSnapshotTokenUsesStoredAttributesWithoutExposingThem(bool secured)
    {
        await fixture.ExecuteAsync($"UPDATE {_schema}.features SET attributes = attributes || '{{\"storedOnly\":\"internal-value\"}}'::jsonb WHERE layer_id = 1 AND objectid = 1;");
        var feature = await CreateReader(secured: secured).GetAsync(1, 1);

        feature.Should().NotBeNull();
        var visible = feature!.Value;
        var stored = Feature.Create(1, visible.Geometry, new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["count"] = 1L,
            ["tenant"] = "a",
            ["secret"] = "base-one",
            ["storedOnly"] = "internal-value"
        }.ToImmutableDictionary());
        visible.ReadStateToken.Should().Be(FeatureStateToken.Compute(stored));
        visible.ReadStateToken.Should().NotBe(FeatureStateToken.Compute(visible));
        visible.Attributes.Should().NotContainKey("storedOnly");
        var json = JsonSerializer.Serialize(visible);
        json.Should().NotContain("internal-value").And.NotContain("ReadStateToken");
        if (secured)
        {
            visible.Attributes.Should().NotContainKey("secret");
            json.Should().NotContain("base-one");
        }
    }

    [Fact]
    public async Task QueryAsync_IncludeReadStateToken_AbsentHiddenNullableField_MatchesRawStoredRowPrecondition()
    {
        var reader = CreateManagedReaderWithHiddenNullableField();

        var result = await reader.QueryAsync(1, new FeatureQuery
        {
            ObjectIds = [1],
            Limit = 1,
            IncludeReadStateToken = true
        });

        result.Items.Should().ContainSingle();
        var visible = result.Items[0];
        visible.Attributes.Should().NotContainKey("hidden_note");

        var rawStored = Feature.Create(1, visible.Geometry, new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["count"] = 1L,
            ["tenant"] = "a",
            ["secret"] = "base-one"
        }.ToImmutableDictionary());
        var projectedWithNull = rawStored with { Attributes = rawStored.Attributes.Add("hidden_note", null) };

        visible.ReadStateToken.Should().Be(FeatureStateToken.Compute(rawStored));
        visible.ReadStateToken.Should().NotBe(FeatureStateToken.Compute(projectedWithNull));

        var precondition = new FeatureEditPrecondition
        {
            ObjectId = 1,
            ExpectedStateToken = FeatureStateToken.FromReadSnapshot(visible)
        };
        var validated = FeatureDataAccess.ValidatePreconditionAgainstRow(1, 1, rawStored, precondition);
        validated.Should().Be(rawStored);
    }

    private PostgresStorageMappedFeatureReader CreateManagedReaderWithHiddenNullableField()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "hidden-nullable-test", Name = "Hidden nullable test" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new() { Name = "geometry", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
                new() { Name = "count", Type = MetadataV2FieldType.Integer },
                new() { Name = "tenant", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String },
                new() { Name = "hidden_note", Type = MetadataV2FieldType.String, Nullable = true, Hidden = true }
            ]
        };
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
        return new PostgresStorageMappedFeatureReader(
            new FixtureConnectionProvider(fixture.ConnectionString),
            pool,
            resource,
            new FeatureStorageMapping(
                "features",
                SchemaName: _schema,
                GeometryColumn: "geometry",
                StorageSrid: 4326,
                AttributesColumn: "attributes",
                LayerDiscriminatorColumn: "layer_id",
                LayerDiscriminatorValue: 1,
                ProviderOptions: new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = "true" }),
            connection: null,
            connectionEncryptionService: null,
            managedFeatureSchema: _schema);
    }
}
