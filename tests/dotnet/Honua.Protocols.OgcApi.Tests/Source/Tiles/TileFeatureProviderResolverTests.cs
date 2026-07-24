// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.Ogc.Api.Tiles.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Unit tests for <see cref="TileFeatureProviderResolver"/> (honua-server#2962), mirroring
/// <c>ODataFeatureProviderResolverTests</c>: a collection whose storage binding names a
/// secondary/additional provider connection must route to that provider's reader/tile
/// capability rather than the DI-registered primary reader/tile provider.
/// </summary>
public sealed class TileFeatureProviderResolverTests
{
    [Fact]
    public async Task ResolveFeatureReaderAsync_SecondaryProviderConnection_RoutesToSecondaryReader()
    {
        var connectionId = Guid.NewGuid();
        var secondaryReader = Substitute.For<IFeatureReader>();
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        secondaryProvider.Reader.Returns(secondaryReader);
        var router = CreateRouter(connectionId, secondaryProvider);
        var resolver = new TileFeatureProviderResolver(router);
        var fallbackReader = Substitute.For<IFeatureReader>();
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var resolved = await resolver.ResolveFeatureReaderAsync(
            snapshot, service, resource, publication, 41, fallbackReader, CancellationToken.None);

        resolved.Should().BeSameAs(secondaryReader);
        await fallbackReader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    [Fact]
    public async Task ResolveFeatureReaderWithStorageAsync_UsesRoutedBindingsPhysicalSrid()
    {
        var connectionId = Guid.NewGuid();
        var secondaryReader = Substitute.For<IFeatureReader>();
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        secondaryProvider.Reader.Returns(secondaryReader);
        var resolver = new TileFeatureProviderResolver(CreateRouter(connectionId, secondaryProvider));
        var fallbackReader = Substitute.For<IFeatureReader>();
        var (snapshot, service, resource, publication) = CreateSnapshot(
            connectionId.ToString(),
            storageSrid: 3857);

        var resolution = await resolver.ResolveFeatureReaderWithStorageAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            fallbackReader,
            fallbackStorageSrid: 4326,
            CancellationToken.None);

        resolution.Reader.Should().BeSameAs(secondaryReader);
        resolution.StorageSrid.Should().Be(3857);
    }

    [Fact]
    public async Task ResolveFeatureReaderAsync_ResourcePrimaryBinding_RoutesToSecondaryReader()
    {
        var connectionId = Guid.NewGuid();
        var secondaryReader = Substitute.For<IFeatureReader>();
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.SqlServer);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(secondaryReader);
        var fallbackReader = Substitute.For<IFeatureReader>();
        var resolver = new TileFeatureProviderResolver(CreateRouter(connectionId, provider));
        var (snapshot, service, resource, publication) = CreateSnapshot(
            connectionId.ToString(),
            publicationUsesExplicitBinding: false,
            resourceUsesPrimaryBinding: true);

        var resolved = await resolver.ResolveFeatureReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            fallbackReader,
            CancellationToken.None);

        resolved.Should().BeSameAs(secondaryReader);
        await fallbackReader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    [Fact]
    public async Task ResolveFeatureReaderAsync_LocalBindingWithoutConnection_UsesFallbackReader()
    {
        var fallbackReader = Substitute.For<IFeatureReader>();
        var resolver = new TileFeatureProviderResolver(providerQueryRouter: null);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId: null);

        var resolved = await resolver.ResolveFeatureReaderAsync(
            snapshot, service, resource, publication, 41, fallbackReader, CancellationToken.None);

        resolved.Should().BeSameAs(fallbackReader);
    }

    [Fact]
    public async Task ResolveFeatureReaderAsync_RoutedBindingWithoutRouter_FailsClosed()
    {
        var connectionId = Guid.NewGuid();
        var resolver = new TileFeatureProviderResolver(providerQueryRouter: null);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var act = () => resolver.ResolveFeatureReaderAsync(
            snapshot, service, resource, publication, 41, Substitute.For<IFeatureReader>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*routing is not configured*");
    }

    [Fact]
    public async Task ResolveTileProviderAsync_SecondaryProviderBindsTileProviderToRoutedConnection()
    {
        var connectionId = Guid.NewGuid();
        var secondaryProvider = Substitute.For<IFeatureDataProvider, IBindableTileProvider>();
        var boundTileProvider = Substitute.For<ITileProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.Postgis);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        ((IBindableTileProvider)secondaryProvider).CreateTileProviderForBinding(
                Arg.Is<FeatureProviderBinding>(binding =>
                    binding.Connection != null && binding.Connection.ConnectionId == connectionId))
            .Returns(boundTileProvider);
        var router = CreateRouter(connectionId, secondaryProvider);
        var resolver = new TileFeatureProviderResolver(router);
        var fallbackProvider = Substitute.For<ITileProvider>();
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var resolution = await resolver.ResolveTileProviderAsync(
            snapshot, service, resource, publication, 41, fallbackProvider, CancellationToken.None);

        resolution.Provider.Should().BeSameAs(boundTileProvider);
        resolution.UnsupportedProviderName.Should().BeNull();
        ((IBindableTileProvider)secondaryProvider).Received(1).CreateTileProviderForBinding(
            Arg.Is<FeatureProviderBinding>(binding =>
                binding.StorageLayerId == 41
                && binding.Connection != null
                && binding.Connection.ConnectionId == connectionId));
    }

    [Fact]
    public async Task ResolveTileProviderAsync_SecondaryProviderWithoutTileSupport_ReturnsUnsupportedProviderName()
    {
        var connectionId = Guid.NewGuid();
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        secondaryProvider.Reader.Returns(Substitute.For<IFeatureReader>());
        var router = CreateRouter(connectionId, secondaryProvider);
        var resolver = new TileFeatureProviderResolver(router);
        var fallbackProvider = Substitute.For<ITileProvider>();
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var resolution = await resolver.ResolveTileProviderAsync(
            snapshot, service, resource, publication, 41, fallbackProvider, CancellationToken.None);

        resolution.Provider.Should().BeNull();
        resolution.UnsupportedProviderName.Should().Be(DataProviderNames.SqlServer);
        await fallbackProvider.DidNotReceiveWithAnyArgs().GetMvtTileAsync(
            default, default, default, default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task SupportsVectorTilesAsync_SecondaryProviderWithoutTileSupport_ReturnsFalse()
    {
        var connectionId = Guid.NewGuid();
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        secondaryProvider.Reader.Returns(Substitute.For<IFeatureReader>());
        var resolver = new TileFeatureProviderResolver(CreateRouter(connectionId, secondaryProvider));
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var supported = await resolver.SupportsVectorTilesAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            CancellationToken.None);

        supported.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveTileProviderAsync_ResourceFirstBinding_BindsSecondaryProvider()
    {
        var connectionId = Guid.NewGuid();
        var provider = Substitute.For<IFeatureDataProvider, IBindableTileProvider>();
        var boundTileProvider = Substitute.For<ITileProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        ((IBindableTileProvider)provider).CreateTileProviderForBinding(
                Arg.Is<FeatureProviderBinding>(binding =>
                    binding.Connection != null && binding.Connection.ConnectionId == connectionId))
            .Returns(boundTileProvider);
        var fallbackProvider = Substitute.For<ITileProvider>();
        var resolver = new TileFeatureProviderResolver(CreateRouter(connectionId, provider));
        var (snapshot, service, resource, publication) = CreateSnapshot(
            connectionId.ToString(),
            publicationUsesExplicitBinding: false);

        var resolution = await resolver.ResolveTileProviderAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            fallbackProvider,
            CancellationToken.None);

        resolution.Provider.Should().BeSameAs(boundTileProvider);
        resolution.UnsupportedProviderName.Should().BeNull();
        ((IBindableTileProvider)provider).Received(1).CreateTileProviderForBinding(
            Arg.Is<FeatureProviderBinding>(binding =>
                binding.StorageBinding.Metadata.Id == "binding-secondary"));
    }

    [Fact]
    public async Task ResolveTileProviderAsync_RoutedBindingWithoutRouter_FailsClosed()
    {
        var connectionId = Guid.NewGuid();
        var resolver = new TileFeatureProviderResolver(providerQueryRouter: null);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var act = () => resolver.ResolveTileProviderAsync(
            snapshot, service, resource, publication, 41, Substitute.For<ITileProvider>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*routing is not configured*");
    }

    private static FeatureProviderQueryRouter CreateRouter(Guid connectionId, IFeatureDataProvider provider)
    {
        var providerName = provider.ProviderName;
        var connectionRegistry = Substitute.For<ISecureConnectionRegistry>();
        connectionRegistry.GetConnectionAsync(connectionId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new DataConnection
            {
                ConnectionId = connectionId,
                Name = "routed-provider",
                Host = "provider.example.test",
                Port = 1433,
                DatabaseName = "spatial",
                Username = "honua",
                Provider = providerName,
                SecretRef = "env:HONUA_TEST_PROVIDER",
                SecretType = "environment",
                CreatedBy = "test"
            });
        return new FeatureProviderQueryRouter(
            connectionRegistry,
            new FeatureDataProviderRegistry([provider]));
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot(
            string? connectionId,
            bool publicationUsesExplicitBinding = true,
            bool resourceUsesPrimaryBinding = false,
            int? storageSrid = null)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-tiles", Name = "Tiles" },
            Protocols = ["OgcApiTiles"],
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-secondary", Name = "secondary" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-secondary"],
            PrimaryStorageBindingId = resourceUsesPrimaryBinding ? "binding-secondary" : null,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "id",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                }
            ]
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-secondary", Name = "secondary" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "dbo.secondary",
            StorageLayerId = 41,
            Options = storageSrid.HasValue
                ? new Dictionary<string, JsonElement>
                {
                    ["storageSrid"] = JsonSerializer.SerializeToElement(storageSrid.Value)
                }
                : new Dictionary<string, JsonElement>()
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-secondary", Name = "secondary" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = publicationUsesExplicitBinding ? binding.Metadata.Id : null,
            LayerIndex = 4
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Services = [service],
            Resources = [resource],
            StorageBindings = [binding],
            Connections = connectionId is null
                ? []
                :
                [
                    new MetadataV2Connection
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = connectionId, Name = "secondary" },
                        Provider = DataProviderNames.SqlServer
                    }
                ],
            Publications = [publication]
        };
        return (new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }
}
