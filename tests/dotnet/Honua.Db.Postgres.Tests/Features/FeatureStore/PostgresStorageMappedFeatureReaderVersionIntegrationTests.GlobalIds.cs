// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportsManagedWritesAsync_GlobalIdIndexMissing_RefusesManagedConnections(bool explicitConnection)
    {
        var connection = explicitConnection ? new DataConnection
        {
            Id = "managed-explicit", ConnectionString = fixture.ConnectionString, IsEncrypted = false
        } : null;
        var reader = CreateReader(connection: connection, globalIds: true);
        (await reader.SupportsManagedWritesAsync(1)).Should().BeFalse();
        // Read support does not require or provision a write constraint.
        (await reader.SupportsBranchVersioningAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong-field", false)]
    [InlineData("wrong-layer", false)]
    [InlineData("nonunique", false)]
    [InlineData("nulls-not-distinct", false)]
    public async Task SupportsManagedWritesAsync_ExplicitManagedConnection_RequiresExactUniqueIndex(string indexCase, bool supported)
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
        var reader = CreateReader(connection: new DataConnection
        {
            Id = "managed-explicit", ConnectionString = fixture.ConnectionString, IsEncrypted = false
        }, globalIds: true);
        (await reader.SupportsManagedWritesAsync(1)).Should().Be(supported);
    }
}
