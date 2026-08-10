// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Admin;

namespace Honua.Postgres.Tests.Features.Admin;

public sealed class PostgreSqlLayerPublishingServiceSqlTests
{
    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithSameServiceIndex_UsesGlobalStorageIdentity()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                }
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha", License = "MIT" },
                    PrimaryStorageBindingId = "binding-alpha"
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-beta", License = "CC0-1.0" },
                    PrimaryStorageBindingId = "binding-beta"
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-alpha" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-beta" },
                    ResourceId = "resource-beta",
                    StorageLayerId = 202
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    ResourceId = "resource-alpha",
                    ServiceId = "service-alpha",
                    StorageBindingId = "binding-alpha",
                    LayerIndex = 0,
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer
                },
                new MetadataV2Publication
                {
                    ResourceId = "resource-beta",
                    ServiceId = "service-beta",
                    StorageBindingId = "binding-beta",
                    LayerIndex = 0,
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer
                }
            ]
        };

        var alpha = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "alpha");
        var beta = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "beta");

        alpha.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, MetadataV2ObjectMetadata>(
            101,
            graph.Resources[0].Metadata));
        beta.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, MetadataV2ObjectMetadata>(
            202,
            graph.Resources[1].Metadata));
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithProtocolSpecificNameCollision_UsesFeatureServerResource()
    {
        var featureMetadata = new MetadataV2ObjectMetadata { Id = "resource-feature", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-ogc", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.OgcApiFeatures
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-ogc", License = "MIT" } },
                new MetadataV2Resource { Metadata = featureMetadata }
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
                CreatePublication("pub-ogc", "service-ogc", "resource-ogc", "binding-ogc", 7, MetadataV2PublicationType.OgcCollection),
                CreatePublication("pub-feature", "service-feature", "resource-feature", "binding-feature", 7, MetadataV2PublicationType.EsriFeatureLayer)
            ]
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, featureMetadata));
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithProtocolAggregateAndDedicatedFeatureServices_UsesDedicatedFeatureResource()
    {
        var featureMetadata = new MetadataV2ObjectMetadata { Id = "resource-feature", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-aggregate", Name = "shared" },
                    Protocols = [ServiceProtocols.FeatureServer, ServiceProtocols.OgcFeatures]
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "shared" },
                    Protocols = [ServiceProtocols.FeatureServer]
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-aggregate", License = "MIT" } },
                new MetadataV2Resource { Metadata = featureMetadata }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-aggregate" },
                    ResourceId = "resource-aggregate",
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
                CreatePublication("pub-aggregate", "service-aggregate", "resource-aggregate", "binding-aggregate", 7, MetadataV2PublicationType.OgcCollection),
                CreatePublication("pub-feature", "service-feature", "resource-feature", "binding-feature", 7, MetadataV2PublicationType.EsriFeatureLayer)
            ]
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, featureMetadata));
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithMixedPublicationsOnFeatureService_UsesEsriResource()
    {
        var featureMetadata = new MetadataV2ObjectMetadata { Id = "resource-feature", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-ogc", License = "MIT" } },
                new MetadataV2Resource { Metadata = featureMetadata }
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
                CreatePublication("pub-ogc", "service-feature", "resource-ogc", "binding-ogc", 7, MetadataV2PublicationType.OgcCollection),
                CreatePublication("pub-feature", "service-feature", "resource-feature", "binding-feature", 7, MetadataV2PublicationType.EsriFeatureLayer)
            ]
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, featureMetadata));
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithTargetIndexCollision_RejectsAmbiguousStorageRoute()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00Z", CultureInfo.InvariantCulture);
        var governedMetadata = new MetadataV2ObjectMetadata
        {
            Id = "resource-alpha",
            Name = "Governed parcels",
            License = "CC-BY-4.0",
            Attribution = "County GIS",
            Publisher = "County GIS"
        };
        var graph = new MetadataV2Graph
        {
            Revision = 4,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" },
                    PublicationIds = ["pub-alpha-101", "pub-stac-alpha-101"]
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    PublicationIds = ["pub-beta-101"]
                }
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = governedMetadata,
                    PrimaryStorageBindingId = "binding-alpha",
                    StorageBindingIds = ["binding-alpha"]
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-beta", License = "MIT" },
                    PrimaryStorageBindingId = "binding-beta",
                    StorageBindingIds = ["binding-beta"]
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-alpha" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-beta" },
                    ResourceId = "resource-beta",
                    StorageLayerId = 202
                }
            ],
            Publications =
            [
                CreatePublication(
                    "pub-alpha-101",
                    "service-alpha",
                    "resource-alpha",
                    "binding-alpha",
                    101,
                    MetadataV2PublicationType.EsriFeatureLayer),
                CreatePublication(
                    "pub-stac-alpha-101",
                    "service-alpha",
                    "resource-alpha",
                    "binding-alpha",
                    101,
                    MetadataV2PublicationType.StacCollection),
                CreatePublication(
                    "pub-beta-101",
                    "service-beta",
                    "resource-beta",
                    "binding-beta",
                    101,
                    MetadataV2PublicationType.OgcCollection)
            ]
        };

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Governed parcels",
            4326,
            now);

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.LayerId.Should().Be(101);
        exception.Message.Should().Contain("already publishes FeatureServer layer 101");
        graph.Publications.Where(publication =>
                publication.PublicationType == MetadataV2PublicationType.StacCollection)
            .Should().ContainSingle().Which.Metadata.Id.Should().Be("pub-stac-alpha-101");
        graph.Publications.Single(publication => publication.Metadata.Id == "pub-beta-101")
            .ResourceId.Should().Be("resource-beta");
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithExistingPublication_PreservesAuthoredIdentityAndSettings()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00Z", CultureInfo.InvariantCulture);
        var existingPublication = CreatePublication(
            "legacy-imported-parcels",
            "service-beta",
            "resource-alpha",
            "binding-alpha",
            101,
            MetadataV2PublicationType.EsriFeatureLayer) with
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "legacy-imported-parcels",
                Name = "legacy-parcels",
                Title = "Authored parcels title"
            },
            TitleOverride = "Authored override",
            IsPrimary = false
        };
        var authoredService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "service-beta",
                Name = "beta",
                Title = "Authored service title",
                UpdatedAt = DateTimeOffset.Parse("2025-03-04T05:06:07Z", CultureInfo.InvariantCulture)
            },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = "/custom/authored/route",
            Protocols = [ServiceProtocols.FeatureServer]
        };
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Services = [authoredService],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" },
                    PrimaryStorageBindingId = "binding-alpha",
                    StorageBindingIds = ["binding-alpha"]
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-alpha" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101
                }
            ],
            Publications = [existingPublication]
        };

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Replacement title must not overwrite authored settings",
            4326,
            now);

        updated.Should().NotBeNull();
        updated!.Revision.Should().Be(8);
        updated.GeneratedAt.Should().Be(now);
        updated.Publications.Should().ContainSingle().Which.Should().BeSameAs(existingPublication);
        var updatedService = updated.Services.Should().ContainSingle().Which;
        updatedService.Metadata.Should().BeSameAs(authoredService.Metadata);
        updatedService.ServiceType.Should().Be(authoredService.ServiceType);
        updatedService.Route.Should().Be(authoredService.Route);
        updatedService.Protocols.Should().Equal(authoredService.Protocols);
        updatedService.Status.Should().BeSameAs(authoredService.Status);
        updatedService.PublicationIds.Should().Equal("legacy-imported-parcels");
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithDuplicateStorageHandles_PrefersCanonicalBindingIdentity()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-wrong" } },
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-canonical" } }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "legacy-binding" },
                    ResourceId = "resource-wrong",
                    StorageLayerId = 101
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-101" },
                    ResourceId = "resource-canonical",
                    StorageLayerId = 101
                }
            ]
        };

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Canonical layer",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        updated.Should().NotBeNull();
        var publication = updated!.Publications.Should().ContainSingle().Which;
        publication.ResourceId.Should().Be("resource-canonical");
        publication.StorageBindingId.Should().Be("binding-layer-101");
        publication.LayerIndex.Should().Be(101);
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithAmbiguousLegacyStorageHandles_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "legacy-binding-a" },
                    ResourceId = "resource-a",
                    StorageLayerId = 101
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "legacy-binding-b" },
                    ResourceId = "resource-b",
                    StorageLayerId = 101
                }
            ]
        };

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Ambiguous layer",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.LayerId.Should().Be(101);
        exception.Message.Should().Contain("multiple legacy storage bindings");
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithProtocolSpecificNameCollision_SelectsFeatureServer()
    {
        var ogcService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-ogc", Name = "beta" },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = "/ogc/features"
        };
        var featureService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "beta" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = "/custom/beta/FeatureServer"
        };
        var graph = new MetadataV2Graph
        {
            Services = [ogcService, featureService],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" } }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-101" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101
                }
            ]
        };

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Parcels",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        updated.Should().NotBeNull();
        updated!.Services.Single(service => service.Metadata.Id == "service-ogc")
            .Should().BeSameAs(ogcService);
        var updatedFeatureService = updated.Services.Single(service => service.Metadata.Id == "service-feature");
        updatedFeatureService.Route.Should().Be("/custom/beta/FeatureServer");
        updatedFeatureService.PublicationIds.Should().ContainSingle();
        updated.Publications.Should().ContainSingle().Which.ServiceId.Should().Be("service-feature");
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithAmbiguousFeatureServerName_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature-a", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature-b", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" } }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-101" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101
                }
            ]
        };

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Parcels",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique Esri FeatureServer service");
    }

    private static MetadataV2Publication CreatePublication(
        string id,
        string serviceId,
        string resourceId,
        string bindingId,
        int layerIndex,
        MetadataV2PublicationType publicationType)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = layerIndex.ToString(CultureInfo.InvariantCulture) },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = bindingId,
            PublicationType = publicationType,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIndex.ToString(CultureInfo.InvariantCulture),
                IsNumeric = true
            }
        };

    [Fact]
    public void BuildAttributesExpression_WithWideTables_ChunksJsonbBuildObjectCalls()
    {
        var columns = Enumerable.Range(1, 51)
            .Select(index => new ColumnInfo
            {
                Name = $"field_{index}",
                DataType = "text"
            })
            .ToArray();

        var method = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "BuildAttributesExpression",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [columns])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', src.\"field_1\"");
        expression.Should().Contain("'field_51', src.\"field_51\"");
    }
}
