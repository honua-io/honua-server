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
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" }
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" }
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
                    LayerIndex = 0
                },
                new MetadataV2Publication
                {
                    ResourceId = "resource-beta",
                    ServiceId = "service-beta",
                    StorageBindingId = "binding-beta",
                    LayerIndex = 0
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
                    MetadataV2PublicationType.EsriFeatureLayer)
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
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" }
                }
            ],
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
        updated.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal("legacy-imported-parcels");
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
