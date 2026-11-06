// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderVersionCapabilityTests
{
    [Theory]
    [InlineData("missing-column")]
    [InlineData("wrong-column")]
    [InlineData("missing-value")]
    public async Task ManagedPartitionGuard_MalformedMapping_RefusesReadsBeforeOpeningDataConnection(string shape)
    {
        var mapping = shape switch
        {
            "missing-column" => ManagedMapping with { LayerDiscriminatorColumn = null },
            "wrong-column" => ManagedMapping with { LayerDiscriminatorColumn = "collection_id" },
            _ => ManagedMapping with { LayerDiscriminatorValue = null }
        };
        var (reader, provider) = CreateReader(mapping);

        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryAsync(7, new FeatureQuery()));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetAsync(7, 42));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.CountAsync(7, new FeatureQuery()));
        AssertNoDatabaseOpened(provider);
    }
}
