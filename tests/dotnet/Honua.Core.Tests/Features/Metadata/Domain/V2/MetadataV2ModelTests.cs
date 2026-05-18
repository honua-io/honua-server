// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Tests for the Metadata v2 core model scaffold.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class MetadataV2ModelTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void Graph_Defaults_AreEmptyResourceFirstCollections()
    {
        var graph = new MetadataV2Graph();

        graph.SchemaVersion.Should().Be(MetadataV2Constants.SchemaVersion);
        graph.ApiVersion.Should().Be(MetadataV2Constants.ApiVersion);
        graph.Revision.Should().Be(0);
        graph.Environment.Should().BeEmpty();
        graph.GeneratedAt.Should().Be(DateTimeOffset.UnixEpoch);
        graph.Namespaces.Should().BeEmpty();
        graph.Catalogs.Should().BeEmpty();
        graph.Resources.Should().BeEmpty();
        graph.Connections.Should().BeEmpty();
        graph.StorageBindings.Should().BeEmpty();
        graph.Services.Should().BeEmpty();
        graph.Publications.Should().BeEmpty();
        graph.ProjectionProfiles.Should().BeEmpty();
        graph.Policies.Should().BeEmpty();
        graph.Roles.Should().BeEmpty();
        graph.Runtime.Should().NotBeNull();
        graph.ExtensionPoints.Should().BeEmpty();
        graph.Extensions.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Records_WithExpression_PreservesOriginalValues()
    {
        var original = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "parcels",
                Name = "parcels",
                Labels = new Dictionary<string, string>
                {
                    ["domain"] = "cadastre"
                }
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds =
            [
                "storage.parcels.postgis",
                "storage.parcels.parquet"
            ],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = "geometry",
                    SemanticRoles =
                    [
                        "geometry.primary"
                    ]
                }
            ],
            PolicyIds =
            [
                "policy.internal"
            ]
        };

        var updated = original with
        {
            Metadata = original.Metadata with
            {
                Name = "parcels-v2"
            }
        };

        original.Metadata.Name.Should().Be("parcels");
        updated.Metadata.Name.Should().Be("parcels-v2");
        updated.Metadata.Id.Should().Be("parcels");
        updated.Metadata.Labels.Should().Contain("domain", "cadastre");
        updated.StorageBindingIds.Should().HaveCount(2);
        updated.SchemaFields.Single().SemanticRoles.Should().Contain("geometry.primary");
        updated.PolicyIds.Should().Contain("policy.internal");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Publication_LinksCanonicalResourceToService()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource.parcels",
                Name = "parcels"
            }
        };

        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "service.public"
            },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures
        };

        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "publication.parcels"
            },
            ResourceId = resource.Metadata.Id,
            ServiceId = service.Metadata.Id,
            StorageBindingId = "storage.parcels.postgis",
            PublicationType = MetadataV2PublicationType.OgcCollection,
            Path = "/collections/parcels",
            LayerIndex = 0,
            SupportedFormats =
            [
                "application/geo+json"
            ],
            FieldAliases = new Dictionary<string, string>
            {
                ["shape"] = "Geometry"
            }
        };

        var graph = new MetadataV2Graph
        {
            Resources = [resource],
            Services = [service],
            Publications = [publication]
        };

        graph.Publications.Single().ResourceId.Should().Be("resource.parcels");
        graph.Publications.Single().ServiceId.Should().Be("service.public");
        graph.Publications.Single().PublicationType.Should().Be(MetadataV2PublicationType.OgcCollection);
        graph.Publications.Single().Path.Should().Be("/collections/parcels");
        graph.Publications.Single().FieldAliases.Should().Contain("shape", "Geometry");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void StorageBinding_Supports_ReturnsDeclaredCapabilityState()
    {
        var binding = new MetadataV2StorageBinding
        {
            ResourceId = "resource.parcels",
            StorageType = MetadataV2StorageType.RelationalTable,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate,
                MetadataV2StorageBindingCapability.Search
            ]
        };

        binding.Supports(MetadataV2StorageBindingCapability.Query).Should().BeTrue();
        binding.Supports(MetadataV2StorageBindingCapability.Aggregate).Should().BeTrue();
        binding.Supports(MetadataV2StorageBindingCapability.Edit).Should().BeFalse();
        binding.Supports(MetadataV2StorageBindingCapability.Transactions).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void StorageTypes_AreSeparateFromServiceAndPublicationTypes()
    {
        var storageNames = Enum.GetNames<MetadataV2StorageType>();
        var serviceNames = Enum.GetNames<MetadataV2ServiceType>();
        var publicationNames = Enum.GetNames<MetadataV2PublicationType>();

        storageNames.Should().Contain(nameof(MetadataV2StorageType.GeoParquet));
        storageNames.Should().Contain(nameof(MetadataV2StorageType.CloudOptimizedGeoTiff));
        serviceNames.Intersect(storageNames).Should().BeEmpty();
        publicationNames.Intersect(storageNames).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ResourceAndPublicationTypes_MatchCanonicalBacklogVocabulary()
    {
        Enum.GetNames<MetadataV2ResourceType>().Should().BeEquivalentTo(
            nameof(MetadataV2ResourceType.FeatureDataset),
            nameof(MetadataV2ResourceType.RasterDataset),
            nameof(MetadataV2ResourceType.Table),
            nameof(MetadataV2ResourceType.TileDataset),
            nameof(MetadataV2ResourceType.Process),
            nameof(MetadataV2ResourceType.Style),
            nameof(MetadataV2ResourceType.Document),
            nameof(MetadataV2ResourceType.ExternalResource));

        Enum.GetNames<MetadataV2PublicationType>().Should().Contain(
            [
                nameof(MetadataV2PublicationType.OgcCollection),
                nameof(MetadataV2PublicationType.WfsFeatureType),
                nameof(MetadataV2PublicationType.WmsLayer),
                nameof(MetadataV2PublicationType.WmtsLayer),
                nameof(MetadataV2PublicationType.EsriFeatureLayer),
                nameof(MetadataV2PublicationType.EsriMapLayer),
                nameof(MetadataV2PublicationType.EsriImageLayer),
                nameof(MetadataV2PublicationType.StacCollection),
                nameof(MetadataV2PublicationType.DcatDistribution),
                nameof(MetadataV2PublicationType.OgcRecord),
                nameof(MetadataV2PublicationType.ODataEntitySet)
            ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void JsonContext_SerializesHyphenatedStorageAndCapabilityNames()
    {
        var binding = new MetadataV2StorageBinding
        {
            ResourceId = "resource.elevation",
            StorageType = MetadataV2StorageType.CloudOptimizedGeoTiff,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Render,
                MetadataV2StorageBindingCapability.Tile,
                MetadataV2StorageBindingCapability.Download
            ]
        };

        var json = JsonSerializer.Serialize(binding, MetadataV2JsonContext.Default.MetadataV2StorageBinding);

        json.Should().Contain("\"storageType\":\"cloud-optimized-geotiff\"");
        json.Should().Contain("\"render\"");
        json.Should().Contain("\"tile\"");
        json.Should().Contain("\"download\"");
    }
}
