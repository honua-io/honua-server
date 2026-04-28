// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class FeatureProviderQueryRouterTests
{
    [Fact]
    public async Task ResolveReaderAsync_ServiceConnectionAndLayerMapping_ReturnsConnectionProviderReader()
    {
        var connectionId = Guid.NewGuid();
        var reader = new Mock<IFeatureReader>(MockBehavior.Strict).Object;
        var provider = CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical, reader);
        var router = CreateRouter(connectionId, DataProviderNames.DuckDb, provider);
        var layer = CreateLayer();
        var service = CreateService(layer, connectionId);

        var resolved = await router.ResolveReaderAsync(
            service,
            layer,
            FeatureProviderReadOperation.Query);

        resolved.Should().BeSameAs(reader);
    }

    [Fact]
    public async Task ResolveReaderAsync_UnsupportedStatisticsOperation_ThrowsClearError()
    {
        var connectionId = Guid.NewGuid();
        var reader = new Mock<IFeatureReader>(MockBehavior.Strict).Object;
        var capabilities = FeatureProviderCapabilities.ReadOnlyAnalytical with
        {
            SupportsStatistics = false
        };
        var provider = CreateProvider(DataProviderNames.DuckDb, capabilities, reader);
        var router = CreateRouter(connectionId, DataProviderNames.DuckDb, provider);
        var layer = CreateLayer();
        var service = CreateService(layer, connectionId);

        var action = () => router.ResolveReaderAsync(
            service,
            layer,
            FeatureProviderReadOperation.Statistics);

        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("does not support statistics operations");
    }

    private static FeatureProviderQueryRouter CreateRouter(
        Guid connectionId,
        string connectionProvider,
        IFeatureDataProvider provider)
    {
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Name = "warehouse",
            Host = "warehouse.example.com",
            Port = 443,
            DatabaseName = "analytics",
            Username = "honua",
            Provider = connectionProvider,
            SecretRef = "env:HONUA_TEST_CONNECTION",
            SecretType = "environment",
            CreatedBy = "test"
        };

        var connections = new Mock<ISecureConnectionRegistry>(MockBehavior.Strict);
        connections
            .Setup(registry => registry.GetConnectionAsync(connectionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var resolver = new FeatureProviderBindingResolver(
            connections.Object,
            new FeatureDataProviderRegistry([provider]));

        return new FeatureProviderQueryRouter(resolver);
    }

    private static IFeatureDataProvider CreateProvider(
        string providerName,
        FeatureProviderCapabilities capabilities,
        IFeatureReader reader)
    {
        var provider = new Mock<IFeatureDataProvider>(MockBehavior.Strict);
        provider.SetupGet(dataProvider => dataProvider.ProviderName).Returns(providerName);
        provider.SetupGet(dataProvider => dataProvider.Capabilities).Returns(capabilities);
        provider.SetupGet(dataProvider => dataProvider.Reader).Returns(reader);
        return provider.Object;
    }

    private static ServiceDefinition CreateService(LayerDefinition layer, Guid connectionId)
        => new(
            "maps",
            "Maps",
            [layer],
            SpatialReference.WGS84,
            ConnectionId: connectionId);

    private static LayerDefinition CreateLayer()
        => LayerDefinition.CreateBasic(1, "roads", GeometryType.LineString) with
        {
            StorageMapping = new LayerStorageMapping(
                "roads",
                SchemaName: "public",
                PrimaryKeyColumn: FieldNames.ObjectId,
                GeometryColumn: "shape",
                StorageSrid: 4326)
        };
}
