// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using NSubstitute;

namespace Honua.TestKit.OData;

/// <summary>Pure provider-routing test data shared by unit and hosted OData suites.</summary>
public static class ODataProviderTestData
{
    public static FeatureProviderQueryRouter CreateRouter(Guid connectionId, IFeatureDataProvider provider)
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

    public static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot(string? connectionId)
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
