// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    private sealed record FailedFeatureLayerAddition(
        MetadataV2Service Service,
        MetadataV2Object FailedResource,
        MetadataV2StorageBinding FailedBinding,
        MetadataV2Layer FailedPublication,
        MetadataV2Graph Previous,
        MetadataV2Graph Persisted);

    private sealed record ExistingLayerWithFailedBinding(
        MetadataV2Service Service,
        MetadataV2Object ExistingResource,
        MetadataV2StorageBinding ExistingBinding,
        MetadataV2Layer ExistingPublication,
        MetadataV2Object FailedResource,
        MetadataV2StorageBinding FailedBinding)
    {
        public (MetadataV2Graph Previous, MetadataV2Graph Persisted) CreateGraphs(
            MetadataV2Object previousResource,
            MetadataV2Layer previousPublication)
        {
            var previous = new MetadataV2Graph
            {
                Revision = 7,
                Services = [Service],
                Resources = [previousResource],
                StorageBindings = [ExistingBinding],
                Publications = [previousPublication],
            };
            var persisted = previous with
            {
                Revision = 8,
                Resources = [previousResource, FailedResource],
                StorageBindings = [ExistingBinding, FailedBinding],
            };
            return (previous, persisted);
        }
    }

    private static MetadataV2Object CreateFailedResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
        StorageBindingIds = ["binding-added"],
        PrimaryStorageBindingId = "binding-added",
    };

    private static MetadataV2StorageBinding CreateFailedRelationalBinding(int? storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
        ResourceId = "resource-added",
        StorageType = MetadataV2StorageType.RelationalTable,
        Locator = "failed_table",
        StorageLayerId = storageLayerId,
    };

    private static FailedFeatureLayerAddition CreateFailedFeatureLayerAddition(int? failedStorageLayerId = null)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
        };
        var failedResource = CreateFailedResource();
        var failedBinding = CreateFailedRelationalBinding(failedStorageLayerId);
        var failedPublication = CreatePublication(
            "publication-failed",
            service.Metadata.Id,
            failedResource.Metadata.Id,
            failedBinding.Metadata.Id,
            0,
            MetadataV2LayerType.EsriFeatureLayer);
        var previous = new MetadataV2Graph { Revision = 7, Services = [service] };
        var persisted = previous with
        {
            Revision = 8,
            Services = [service with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        return new(service, failedResource, failedBinding, failedPublication, previous, persisted);
    }

    private static ExistingLayerWithFailedBinding CreateExistingLayerWithFailedBinding()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            PublicationIds = ["publication-existing"],
        };
        var existingResource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-existing" },
            StorageBindingIds = ["binding-existing"],
            PrimaryStorageBindingId = "binding-existing",
        };
        var existingBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-existing" },
            ResourceId = existingResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "existing_table",
            StorageLayerId = 7,
        };
        var existingPublication = CreatePublication(
            "publication-existing",
            service.Metadata.Id,
            existingResource.Metadata.Id,
            existingBinding.Metadata.Id,
            0,
            MetadataV2LayerType.EsriFeatureLayer);
        return new(
            service,
            existingResource,
            existingBinding,
            existingPublication,
            CreateFailedResource(),
            CreateFailedRelationalBinding(42));
    }

    private static MetadataV2Graph CreateSharedNameGovernanceGraph(
        MetadataV2ObjectMetadata featureMetadata,
        IReadOnlyList<MetadataV2Service> services,
        string ogcServiceId) => new()
        {
            Services = services,
            Resources =
            [
                new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "resource-ogc", License = "MIT" } },
                new MetadataV2Object { Metadata = featureMetadata }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-ogc" },
                    ResourceId = "resource-ogc",
                    StorageLayerId = 7
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-feature" },
                    ResourceId = "resource-feature",
                    StorageLayerId = 7
                }
            ],
            Publications =
            [
                CreatePublication("pub-ogc", ogcServiceId, "resource-ogc", "binding-ogc", 7, MetadataV2LayerType.OgcCollection),
                CreatePublication("pub-feature", "service-feature", "resource-feature", "binding-feature", 7, MetadataV2LayerType.EsriFeatureLayer)
            ]
        };

    private static MetadataV2Graph CreateSharedResourceTwoLayerGraph(string serviceId, MetadataV2Status status) => new()
    {
        Revision = 7,
        Resources =
        [
            new MetadataV2Object
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "resource-shared" },
                StorageBindingIds = ["binding-7", "binding-8"],
                PrimaryStorageBindingId = "binding-7",
                Status = status,
            },
        ],
        StorageBindings =
        [
            new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding-7" },
                ResourceId = "resource-shared",
                StorageLayerId = 7,
                Status = status,
            },
            new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding-8" },
                ResourceId = "resource-shared",
                StorageLayerId = 8,
                Status = status,
            },
        ],
        Publications =
        [
            CreatePublication("pub-7", serviceId, "resource-shared", "binding-7", 7,
                MetadataV2LayerType.EsriFeatureLayer) with { Status = status },
            CreatePublication("pub-8", serviceId, "resource-shared", "binding-8", 8,
                MetadataV2LayerType.EsriFeatureLayer) with { Status = status },
        ],
    };

    private static MetadataV2Graph CreateAlphaLayer101Graph(params MetadataV2Service[] services) => new()
    {
        Services = services,
        Resources =
        [
            new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" } },
        ],
        StorageBindings =
        [
            new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-101" },
                ResourceId = "resource-alpha",
                StorageLayerId = 101,
            },
        ],
    };

    private static void AssertLinkedLayer101ToBetaConflicts(MetadataV2Graph graph)
    {
        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Parcels",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.LayerId.Should().Be(101);
        exception.Message.Should().Contain("does not resolve to one unique Esri FeatureServer service");
    }
}
