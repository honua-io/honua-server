// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
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
        graph.Resources.Should().BeEmpty();
        graph.Connections.Should().BeEmpty();
        graph.StorageBindings.Should().BeEmpty();
        graph.Services.Should().BeEmpty();
        graph.Publications.Should().BeEmpty();
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
                    Type = MetadataV2FieldType.Geometry,
                    SemanticRoles =
                    [
                        "geometry.primary"
                    ]
                }
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
            Protocols = [ServiceProtocols.OgcFeatures]
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
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = "0",
                IsNumeric = true,
                PathOverride = "/collections/parcels",
            },
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
    public void EsriServiceLayers_AreServiceLocalPublicationSlots()
    {
        var parcels = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource.parcels",
                Name = "Parcels"
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "parcel_id",
                    Type = MetadataV2FieldType.String,
                    SemanticRoles =
                    [
                        "identifier.primary"
                    ]
                }
            ]
        };

        var hydrants = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource.hydrants",
                Name = "Hydrants"
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "hydrant_id",
                    Type = MetadataV2FieldType.String,
                    SemanticRoles =
                    [
                        "identifier.primary"
                    ]
                }
            ]
        };

        var parcelsPublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "publication.public-works.0",
                Name = "Parcels"
            },
            ResourceId = parcels.Metadata.Id,
            ServiceId = "service.public-works-feature-server",
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = "0",
                IsNumeric = true,
                PathOverride = "/PublicWorks/FeatureServer/0",
            }
        };

        var hydrantsPublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "publication.public-works.1",
                Name = "Hydrants"
            },
            ResourceId = hydrants.Metadata.Id,
            ServiceId = "service.public-works-feature-server",
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = "1",
                IsNumeric = true,
                PathOverride = "/PublicWorks/FeatureServer/1",
            }
        };

        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "service.public-works-feature-server",
                Name = "Public Works FeatureServer"
            },
            Protocols = [ServiceProtocols.FeatureServer],
            Route = "/PublicWorks/FeatureServer"
        };

        var graph = new MetadataV2Graph
        {
            Resources =
            [
                parcels,
                hydrants
            ],
            Services = [service],
            Publications =
            [
                parcelsPublication,
                hydrantsPublication
            ]
        };

        graph.Publications
            .Where(p => p.ServiceId == "service.public-works-feature-server")
            .Select(p => p.Metadata.Id)
            .Should().BeEquivalentTo(
                "publication.public-works.0",
                "publication.public-works.1");
        graph.Services.Single().PrimaryProtocol.Should().Be(ServiceProtocols.FeatureServer);
        graph.Publications.Should().OnlyContain(publication =>
            publication.ServiceId == "service.public-works-feature-server" &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);
        graph.Publications.Select(publication => publication.LayerIndex).Should().BeEquivalentTo(
            new int?[] { 0, 1 });
        graph.Publications.Select(publication => publication.ResourceId).Should().BeEquivalentTo(
            "resource.parcels",
            "resource.hydrants");
        graph.Resources.Select(resource => resource.Metadata.Id).Should().BeEquivalentTo(
            "resource.parcels",
            "resource.hydrants");
        graph.Resources.Should().OnlyContain(resource => resource.SchemaFields.Count == 1);
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
    public void StorageTypes_AreSeparateFromPublicationTypes()
    {
        var storageNames = Enum.GetNames<MetadataV2StorageType>();
        var publicationNames = Enum.GetNames<MetadataV2PublicationType>();

        storageNames.Should().Contain(nameof(MetadataV2StorageType.GeoParquet));
        storageNames.Should().Contain(nameof(MetadataV2StorageType.CloudOptimizedGeoTiff));
        publicationNames.Intersect(storageNames).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ResourceAndPublicationTypes_MatchCanonicalBacklogVocabulary()
    {
        Enum.GetNames<MetadataV2ResourceType>().Should().BeEquivalentTo(
            nameof(MetadataV2ResourceType.FeatureDataset),
            nameof(MetadataV2ResourceType.RasterDataset),
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

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithConsistentResourceFirstReferences_ReturnsValid()
    {
        var graph = CreateValidGraph();

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithNullDeserializedResourceStorageBindingIds_ReturnsValidationError()
    {
        var json = JsonSerializer.Serialize(CreateValidGraph(), MetadataV2JsonContext.Default.MetadataV2Graph)
            .Replace(
                "\"storageBindingIds\":[\"storage.parcels.postgis\"]",
                "\"storageBindingIds\":null",
                StringComparison.Ordinal);
        json.Should().Contain("\"storageBindingIds\":null");
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph);

        var result = MetadataV2GraphValidator.Validate(graph!);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            "resource 'resource.parcels' primary storage binding 'storage.parcels.postgis' must be listed in storageBindingIds.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithDuplicateGraphEntityId_ReturnsError()
    {
        var graph = CreateValidGraph() with
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "resource.parcels"
                    }
                }
            ]
        };

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("metadata id 'resource.parcels' is duplicated.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithDanglingPublicationReferences_ReturnsResourceAndServiceErrors()
    {
        var graph = CreateValidGraph() with
        {
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "publication.dangling"
                    },
                    ResourceId = "resource.missing",
                    ServiceId = "service.missing",
                    StorageBindingId = "storage.missing"
                }
            ]
        };

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            "publication 'publication.dangling' references missing resource 'resource.missing'.");
        result.Errors.Should().Contain(
            "publication 'publication.dangling' references missing service 'service.missing'.");
        result.Errors.Should().Contain(
            "publication 'publication.dangling' references missing storage binding 'storage.missing'.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithStorageBindingOwnedByDifferentResource_ReturnsResourceFirstError()
    {
        var graph = CreateValidGraph() with
        {
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "resource.parcels"
                    },
                    StorageBindingIds =
                    [
                        "storage.hydrants.postgis"
                    ]
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "storage.hydrants.postgis"
                    },
                    ResourceId = "resource.hydrants"
                }
            ]
        };

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            "storage binding 'storage.hydrants.postgis' references missing resource 'resource.hydrants'.");
        result.Errors.Should().Contain(
            "resource 'resource.parcels' references storage binding 'storage.hydrants.postgis' owned by resource 'resource.hydrants'.");
        result.Errors.Should().Contain(
            "resource 'resource.parcels' primary storage binding 'storage.parcels.postgis' must be listed in storageBindingIds.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_WithServiceReferencingForeignPublication_ReturnsError()
    {
        var graph = CreateValidGraph() with
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "service.parcels"
                    }
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "publication.parcels"
                    },
                    ResourceId = "resource.parcels",
                    ServiceId = "service.other",
                    StorageBindingId = "storage.parcels.postgis"
                }
            ]
        };

        var result = MetadataV2GraphValidator.Validate(graph);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("publication 'publication.parcels' references missing service 'service.other'.");
    }

    private static MetadataV2Graph CreateValidGraph()
    {
        return new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "resource.parcels"
                    },
                    StorageBindingIds =
                    [
                        "storage.parcels.postgis"
                    ]
                }
            ],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "connection.postgis"
                    }
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "storage.parcels.postgis"
                    },
                    ResourceId = "resource.parcels",
                    ConnectionId = "connection.postgis"
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "service.parcels"
                    }
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "publication.parcels"
                    },
                    ResourceId = "resource.parcels",
                    ServiceId = "service.parcels",
                    StorageBindingId = "storage.parcels.postgis"
                }
            ]
        };
    }
}
