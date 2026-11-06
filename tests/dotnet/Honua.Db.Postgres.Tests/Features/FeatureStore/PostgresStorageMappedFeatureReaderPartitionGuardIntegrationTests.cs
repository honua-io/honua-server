// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionIntegrationTests
{
    [Theory]
    [InlineData("missing-column")]
    [InlineData("wrong-column")]
    [InlineData("missing-value")]
    public async Task ManagedPartitionGuard_MalformedMapping_RefusesQueryGetAndCount(string shape)
    {
        await AddForeignPartitionRowAsync();
        var mapping = PartitionMapping() with
        {
            LayerDiscriminatorColumn = shape switch
            {
                "missing-column" => null,
                "wrong-column" => "collection_id",
                _ => "layer_id"
            },
            LayerDiscriminatorValue = shape == "missing-value" ? null : 1
        };
        var reader = CreatePartitionReader(mapping, _schema);

        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryAsync(1, new FeatureQuery()));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetAsync(1, 91));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.CountAsync(1, new FeatureQuery()));
    }

    [Fact]
    public async Task ManagedPartitionGuard_CanonicalMapping_ExcludesForeignPartitionRows()
    {
        await AddForeignPartitionRowAsync();
        var reader = CreatePartitionReader(PartitionMapping(), _schema);

        var query = await reader.QueryAsync(1, new FeatureQuery());

        query.Items.Select(item => item.Id).Should().BeEquivalentTo([1L, 2L, 3L]);
        (await reader.CountAsync(1, new FeatureQuery())).Should().Be(3);
        (await reader.GetAsync(1, 91)).Should().BeNull();
    }

    [Fact]
    public async Task ManagedPartitionGuard_ExternalSameNamedTable_PreservesDeclaredSourceReads()
    {
        await AddForeignPartitionRowAsync();
        var reader = CreatePartitionReader(PartitionMapping() with { LayerDiscriminatorColumn = null }, "honua");

        var query = await reader.QueryAsync(1, new FeatureQuery());

        query.Items.Should().HaveCount(5);
        (await reader.CountAsync(1, new FeatureQuery())).Should().Be(5);
        (await reader.GetAsync(1, 91)).Should().NotBeNull();
    }

    private FeatureStorageMapping PartitionMapping()
        => new("features", SchemaName: _schema, GeometryColumn: "geometry", AttributesColumn: "attributes",
            LayerDiscriminatorColumn: "layer_id", LayerDiscriminatorValue: 1,
            ProviderOptions: new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = "true" });
}
