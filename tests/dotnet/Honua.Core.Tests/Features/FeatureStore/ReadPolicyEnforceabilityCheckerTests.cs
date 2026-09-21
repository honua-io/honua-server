// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class ReadPolicyEnforceabilityCheckerTests
{
    private const string WarehouseConnectionId = "conn-warehouse";

    [Fact]
    public void Capabilities_OnlyThePostgisPresetAdvertisesReadPolicyEnforcement()
    {
        FeatureProviderCapabilities.ReadWritePostgis.SupportsReadPolicyEnforcement.Should().BeTrue();
        FeatureProviderCapabilities.ReadOnlyAnalytical.SupportsReadPolicyEnforcement.Should().BeFalse();
        FeatureProviderCapabilities.ReadOnlyMySql.SupportsReadPolicyEnforcement.Should().BeFalse();
        new FeatureProviderCapabilities().SupportsReadPolicyEnforcement.Should().BeFalse();
    }

    [Theory]
    [InlineData("*", "parcels")]
    [InlineData("Maps", "PARCELS")]
    public async Task FindUnenforceableTargetAsync_LayerOnEnforcingProvider_ReturnsNull(string service, string layer)
    {
        var checker = CreateChecker();

        var target = await checker.FindUnenforceableTargetAsync(service, layer);

        target.Should().BeNull();
    }

    [Theory]
    [InlineData("*", "roads")]
    [InlineData("Maps", "Roads")]
    [InlineData("*", "*")]
    [InlineData("maps", "*")]
    public async Task FindUnenforceableTargetAsync_ScopeIncludesLayerOnNonEnforcingProvider_ReturnsThatLayer(string service, string layer)
    {
        var checker = CreateChecker();

        var target = await checker.FindUnenforceableTargetAsync(service, layer);

        target.Should().Be(new UnenforceableReadPolicyTarget("roads", DataProviderNames.DuckDb));
    }

    [Theory]
    [InlineData("OtherService", "roads")]
    [InlineData("*", "not-a-layer")]
    public async Task FindUnenforceableTargetAsync_ScopeTargetsNoLayer_ReturnsNull(string service, string layer)
    {
        var checker = CreateChecker();

        var target = await checker.FindUnenforceableTargetAsync(service, layer);

        target.Should().BeNull();
    }

    [Fact]
    public async Task FindUnenforceableTargetAsync_WithoutMetadata_RequiresEveryRegisteredProviderToEnforce()
    {
        var registry = new FeatureDataProviderRegistry(
        [
            CreateProvider(DataProviderNames.Postgis, FeatureProviderCapabilities.ReadWritePostgis),
            CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical)
        ]);
        var checker = new ReadPolicyEnforceabilityChecker(graphProvider: null, router: null, registry);

        var target = await checker.FindUnenforceableTargetAsync("*", "parcels");

        target.Should().Be(new UnenforceableReadPolicyTarget("parcels", DataProviderNames.DuckDb));
    }

    private static ReadPolicyEnforceabilityChecker CreateChecker()
    {
        var registry = new FeatureDataProviderRegistry(
        [
            CreateProvider(DataProviderNames.Postgis, FeatureProviderCapabilities.ReadWritePostgis),
            CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical)
        ]);

        // The secure connection registry has no materialized connection, so the router falls
        // back to the provider declared on the metadata connection (as it does for reads).
        var connections = new Mock<ISecureConnectionRegistry>();
        var router = new FeatureProviderQueryRouter(connections.Object, registry);

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
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-maps", Name = "Maps" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources =
            [
                CreateResource("res-parcels", "parcels", "binding-parcels", "binding-parcels-alternative"),
                CreateResource("res-roads", "roads", "binding-roads")
            ],
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
                CreateBinding("binding-parcels-alternative", "res-parcels", WarehouseConnectionId, storageLayerId: 11),
                CreateBinding("binding-roads", "res-roads", WarehouseConnectionId, storageLayerId: 2)
            ],
            Services = [service],
            Publications = [CreatePublication("pub-parcels", "res-parcels", "binding-parcels", "1"), CreatePublication("pub-roads", "res-roads", "binding-roads", "2")]
        };

        return new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Resource CreateResource(string id, string name, string bindingId, params string[] alternativeBindingIds) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        Type = MetadataV2ResourceType.FeatureDataset,
        StorageBindingIds = [bindingId, .. alternativeBindingIds]
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

    private static MetadataV2Publication CreatePublication(string id, string resourceId, string bindingId, string identifier) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ServiceId = "svc-maps",
        ResourceId = resourceId,
        StorageBindingId = bindingId,
        Identifier = new MetadataV2PublicationIdentifier { Value = identifier, IsNumeric = true }
    };

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
