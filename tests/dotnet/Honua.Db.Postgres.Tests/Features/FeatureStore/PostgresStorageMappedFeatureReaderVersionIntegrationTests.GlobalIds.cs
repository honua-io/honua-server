// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionIntegrationTests
{
    [Theory]
    [InlineData("implicit")]
    [InlineData("explicit")]
    public async Task SupportsManagedWritesAsync_GlobalIdIndexMissing_RefusesManagedConnections(string connectionKind)
    {
        var connection = connectionKind == "explicit" ? new DataConnection
        {
            Id = "managed-explicit", ConnectionString = fixture.ConnectionString, IsEncrypted = false
        } : null;
        var reader = CreateGlobalIdReader(connection);
        (await reader.SupportsManagedWritesAsync(1)).Should().BeFalse();
        // Read support does not require or provision a write constraint.
        (await reader.SupportsBranchVersioningAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-field")]
    [InlineData("wrong-layer")]
    [InlineData("nonunique")]
    [InlineData("nulls-not-distinct")]
    public async Task SupportsManagedWritesAsync_ExplicitManagedConnection_RequiresExactUniqueIndex(string indexCase)
    {
        await fixture.ExecuteAsync($$"""
            UPDATE "{{_schema}}".features SET attributes = jsonb_build_object(
                'tenant', CASE objectid WHEN 1 THEN '6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f'
                    WHEN 2 THEN 'f65f3d6c-7adc-4c28-a888-b274345b1e14'
                    ELSE 'fb9c8c7a-dc9d-4562-a377-5d60431b9e70' END);
            """);
        var field = indexCase == "wrong-field" ? "other" : "tenant";
        var layer = indexCase == "wrong-layer" ? 2 : 1;
        var unique = indexCase == "nonunique" ? "" : "UNIQUE";
        var nulls = indexCase == "nulls-not-distinct" ? "NULLS NOT DISTINCT" : "";
        await fixture.ExecuteAsync($$"""
            CREATE {{unique}} INDEX idx_features_gid_590ea1835b90577b09e35ea3087e67d1
            ON "{{_schema}}".features ((NULLIF(attributes->>'{{field}}','')::uuid)) {{nulls}} WHERE layer_id = {{layer}};
            """);
        var reader = CreateGlobalIdReader(new DataConnection
        {
            Id = "managed-explicit", ConnectionString = fixture.ConnectionString, IsEncrypted = false
        });
        (await reader.SupportsManagedWritesAsync(1)).Should().Be(indexCase == "valid");
    }

    private PostgresStorageMappedFeatureReader CreateGlobalIdReader(DataConnection? connection)
    {
        var resource = new MetadataV2Object
        {
            Metadata = new() { Id = "global-id-test", Name = "Global ID test" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields = [new() { Name = "tenant", Type = MetadataV2FieldType.Uuid }],
            Editing = new() { GlobalIdField = "tenant" }
        };
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
        return new PostgresStorageMappedFeatureReader(new FixtureConnectionProvider(fixture.ConnectionString),
            pool, resource, new FeatureStorageMapping("features", SchemaName: _schema,
                GeometryColumn: "geometry", StorageSrid: 4326, AttributesColumn: "attributes",
                LayerDiscriminatorColumn: "layer_id", LayerDiscriminatorValue: 1,
                ProviderOptions: new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = "true" }),
            connection: connection, connectionEncryptionService: null, managedFeatureSchema: _schema);
    }
}
