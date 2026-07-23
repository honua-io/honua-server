// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.OData.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed class ODataFeatureProviderResolverTests
{
    [Fact]
    public async Task ResolveReaderAsync_SecondaryProvider_RoutesCountAndRejectsWrites()
    {
        var connectionId = Guid.NewGuid();
        var secondaryReader = Substitute.For<IFeatureReader>();
        secondaryReader.CountAsync(41, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(37L);
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        secondaryProvider.Reader.Returns(secondaryReader);
        secondaryProvider.Writer.Returns((IFeatureWriter?)null);

        var connectionRegistry = Substitute.For<ISecureConnectionRegistry>();
        connectionRegistry.GetConnectionAsync(connectionId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new DataConnection
            {
                ConnectionId = connectionId,
                Name = "secondary-sql-server",
                Host = "sql.example.test",
                Port = 1433,
                DatabaseName = "spatial",
                Username = "honua",
                Provider = DataProviderNames.SqlServer,
                SecretRef = "env:HONUA_TEST_SQLSERVER",
                SecretType = "environment",
                CreatedBy = "test"
            });
        var router = new FeatureProviderQueryRouter(
            connectionRegistry,
            new FeatureDataProviderRegistry([secondaryProvider]));
        var fallbackReader = Substitute.For<IFeatureReader>();
        var fallbackWriter = Substitute.For<IFeatureWriter>();
        var resolver = new ODataFeatureProviderResolver(fallbackReader, fallbackWriter, router);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var resolved = await resolver.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            FeatureProviderReadOperation.Count,
            CancellationToken.None);
        var count = await resolved.CountAsync(41, new FeatureQuery(), CancellationToken.None);
        var writeSupport = await resolver.CheckWriteSupportAsync(
            snapshot,
            service,
            resource,
            publication,
            41,
            CancellationToken.None);

        resolved.Should().BeSameAs(secondaryReader);
        count.Should().Be(37);
        writeSupport.Supported.Should().BeFalse();
        writeSupport.ErrorMessage.Should().Contain("read-only");
        await fallbackReader.DidNotReceiveWithAnyArgs().CountAsync(default, default, default);
    }

    [Fact]
    public async Task ResolveQueryReaderAsync_CountRequestedWithoutCountCapability_RejectsBeforeQuery()
    {
        var connectionId = Guid.NewGuid();
        var secondaryReader = Substitute.For<IFeatureReader>();
        var secondaryProvider = Substitute.For<IFeatureDataProvider>();
        secondaryProvider.ProviderName.Returns(DataProviderNames.SqlServer);
        secondaryProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical with { SupportsCount = false });
        secondaryProvider.Reader.Returns(secondaryReader);
        var router = CreateRouter(connectionId, secondaryProvider);
        var resolver = new ODataFeatureProviderResolver(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            router);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var act = () => resolver.ResolveQueryReaderAsync(
            snapshot, service, resource, publication, 41, requireCount: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*count operations*");
        await secondaryReader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    [Fact]
    public async Task CheckWriteSupportAsync_PrimaryWriter_AllowsExistingCrudPipeline()
    {
        var connectionId = Guid.NewGuid();
        var primaryWriter = Substitute.For<IFeatureWriter>();
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        provider.Reader.Returns(Substitute.For<IFeatureReader>());
        provider.Writer.Returns(primaryWriter);
        var resolver = new ODataFeatureProviderResolver(
            Substitute.For<IFeatureReader>(), primaryWriter, CreateRouter(connectionId, provider));
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var support = await resolver.CheckWriteSupportAsync(
            snapshot, service, resource, publication, 41, CancellationToken.None);

        support.Supported.Should().BeTrue();
    }

    [Fact]
    public async Task CheckWriteSupportAsync_SecondaryWriter_RejectsPrimaryCrudFallback()
    {
        var connectionId = Guid.NewGuid();
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.SqlServer);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        provider.Reader.Returns(Substitute.For<IFeatureReader>());
        provider.Writer.Returns(Substitute.For<IFeatureWriter>());
        var resolver = new ODataFeatureProviderResolver(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            CreateRouter(connectionId, provider));
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var support = await resolver.CheckWriteSupportAsync(
            snapshot, service, resource, publication, 41, CancellationToken.None);

        support.Supported.Should().BeFalse();
        support.ErrorMessage.Should().Contain("secondary");
    }

    [Fact]
    public async Task ResolveReaderAsync_MissingRoutingMetadata_FailsClosed()
    {
        var connectionId = Guid.NewGuid();
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.SqlServer);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(Substitute.For<IFeatureReader>());
        var resolver = new ODataFeatureProviderResolver(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            CreateRouter(connectionId, provider));
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var act = () => resolver.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication with { StorageBindingId = null },
            41,
            FeatureProviderReadOperation.Query,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*routing metadata*");
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
        CreateSnapshot(string connectionId)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-odata", Name = "OData" },
            Protocols = ["OData"],
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-secondary", Name = "secondary" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-secondary"],
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
            StorageLayerId = 41
        };
        var connection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId, Name = "secondary" },
            Provider = DataProviderNames.SqlServer
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-secondary", Name = "secondary" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            LayerIndex = 4
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Services = [service],
            Resources = [resource],
            StorageBindings = [binding],
            Connections = [connection],
            Publications = [publication]
        };
        return (new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }
}
