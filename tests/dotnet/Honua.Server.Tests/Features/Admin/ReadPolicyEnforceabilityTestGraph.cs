// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Moq;

namespace Honua.Server.Tests.Features.Admin;

// A metadata graph with one layer on an enforcing provider ("parcels", PostGIS) and one on a
// provider without read-policy enforcement ("roads", DuckDB), plus the checker built over it.
internal static class ReadPolicyEnforceabilityTestGraph
{
    private const string WarehouseConnectionId = "conn-warehouse";

    internal static ReadPolicyEnforceabilityChecker CreateChecker()
    {
        var registry = new FeatureDataProviderRegistry(
        [
            CreateProvider(DataProviderNames.Postgis, FeatureProviderCapabilities.ReadWritePostgis),
            CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical)
        ]);
        var router = new FeatureProviderQueryRouter(Mock.Of<ISecureConnectionRegistry>(), registry);

        return new ReadPolicyEnforceabilityChecker(new StubGraphProvider(CreateSnapshot()), router, registry);
    }

    private static IFeatureDataProvider CreateProvider(string providerName, FeatureProviderCapabilities capabilities)
    {
        var provider = new Mock<IFeatureDataProvider>(MockBehavior.Strict);
        provider.SetupGet(dataProvider => dataProvider.ProviderName).Returns(providerName);
        provider.SetupGet(dataProvider => dataProvider.Capabilities).Returns(capabilities);
        return provider.Object;
    }

    private static MetadataV2GraphSnapshot CreateSnapshot()
    {
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [CreateResource("res-parcels", "parcels", "binding-parcels"), CreateResource("res-roads", "roads", "binding-roads")],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = WarehouseConnectionId, Name = "warehouse" },
                    Provider = DataProviderNames.DuckDb
                }
            ],
            StorageBindings =
            [
                CreateBinding("binding-parcels", "res-parcels", connectionId: null, storageLayerId: 1),
                CreateBinding("binding-roads", "res-roads", WarehouseConnectionId, storageLayerId: 2)
            ]
        };

        return new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Object CreateResource(string id, string name, string bindingId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        Type = MetadataV2ObjectType.FeatureDataset,
        StorageBindingIds = [bindingId]
    };

    private static MetadataV2StorageBinding CreateBinding(string id, string resourceId, string? connectionId, int storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ResourceId = resourceId,
        ConnectionId = connectionId,
        StorageType = MetadataV2StorageType.RelationalTable,
        Locator = "public." + id,
        StorageLayerId = storageLayerId
    };

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
