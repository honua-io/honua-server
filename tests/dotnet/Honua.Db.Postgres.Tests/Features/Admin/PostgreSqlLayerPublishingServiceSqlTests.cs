// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class PostgreSqlLayerPublishingServiceSqlTests
{
    [Theory]
    [InlineData("committed", true)]
    [InlineData("aborted", false)]
    [InlineData("in progress", null)]
    [InlineData(null, null)]
    public void InterpretTransactionStatus_ClassifiesOnlyTerminalOutcomes(string? status, bool? expected)
    {
        PostgresTransactionOutcomeObserver.InterpretStatus(status).Should().Be(expected);
    }

    [Fact]
    public async Task RetryUnresolvedTransactionObservation_WaitsForTerminalOutcome()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await PostgresTransactionOutcomeObserver.RetryUnresolvedObservationAsync(
            () => Task.FromResult<bool?>(++attempts == 3 ? true : null),
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        result.Should().BeTrue();
        attempts.Should().Be(3);
        delays.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void BuildCompensatingMetadataV2Graph_RestoresContentAtANewRevision()
    {
        var original = new MetadataV2Graph
        {
            Revision = 7,
            Environment = "Production",
            GeneratedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "Resource 1" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                },
            ],
        };
        var compensatedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);

        var compensation = PostgreSqlLayerPublishingService.BuildCompensatingMetadataV2Graph(
            original,
            persistedRevision: 8,
            compensatedAt);

        compensation.Revision.Should().Be(9);
        compensation.GeneratedAt.Should().Be(compensatedAt);
        compensation.Resources.Should().BeEquivalentTo(original.Resources);
        compensation.Services.Should().BeEquivalentTo(original.Services);
        compensation.Publications.Should().BeEquivalentTo(original.Publications);
        compensation.StorageBindings.Should().BeEquivalentTo(original.StorageBindings);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RemovesFailedMutationAndPreservesConcurrentWriter()
    {
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-shared" },
                    PublicationIds = ["pub-original"],
                },
            ],
            Publications =
            [
                CreatePublication(
                    "pub-original",
                    "service-shared",
                    "resource-original",
                    "binding-original",
                    1,
                    MetadataV2PublicationType.EsriFeatureLayer),
            ],
        };
        var persisted = previous with
        {
            Revision = 8,
            Services =
            [
                previous.Services[0] with { PublicationIds = ["pub-original", "pub-failed"] },
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-failed" } },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-failed" },
                    ResourceId = "resource-failed",
                },
            ],
            Publications =
            [
                previous.Publications[0],
                CreatePublication(
                    "pub-failed",
                    "service-shared",
                    "resource-failed",
                    "binding-failed",
                    2,
                    MetadataV2PublicationType.EsriFeatureLayer),
            ],
        };
        var concurrentPublication = CreatePublication(
            "pub-concurrent",
            "service-shared",
            "resource-concurrent",
            "binding-concurrent",
            3,
            MetadataV2PublicationType.EsriFeatureLayer);
        var current = persisted with
        {
            Revision = 9,
            Services =
            [
                persisted.Services[0] with
                {
                    PublicationIds = ["pub-original", "pub-failed", "pub-concurrent"],
                },
            ],
            Resources =
            [
                .. persisted.Resources,
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-concurrent" } },
            ],
            StorageBindings =
            [
                .. persisted.StorageBindings,
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-concurrent" },
                    ResourceId = "resource-concurrent",
                },
            ],
            Publications = [.. persisted.Publications, concurrentPublication],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:00:00Z", CultureInfo.InvariantCulture));

        compensation.Revision.Should().Be(10);
        compensation.Publications.Select(item => item.Metadata.Id)
            .Should().Equal("pub-original", "pub-concurrent");
        compensation.Resources.Select(item => item.Metadata.Id)
            .Should().Equal("resource-concurrent");
        compensation.StorageBindings.Select(item => item.Metadata.Id)
            .Should().Equal("binding-concurrent");
        compensation.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal("pub-original", "pub-concurrent");
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresExistingServiceFieldsThreeWay()
    {
        var originalAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);
        var publishedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);
        var originalCondition = new MetadataV2Condition
        {
            Type = "Original",
            Status = "True",
        };
        var concurrentCondition = new MetadataV2Condition
        {
            Type = "Concurrent",
            Status = "True",
        };
        var previousService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "service-existing",
                Name = "roads",
                Description = "Original service",
                CreatedAt = originalAt,
                UpdatedAt = originalAt,
            },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = "/ogc/features",
            Protocols = [ServiceProtocols.OgcFeatures],
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Draft,
                State = MetadataV2OperationalState.Unknown,
                Conditions = [originalCondition],
                ObservedAt = originalAt,
            },
        };
        var persistedService = previousService with
        {
            Metadata = previousService.Metadata with
            {
                Title = "roads",
                UpdatedAt = publishedAt,
            },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = "/rest/services/roads/FeatureServer",
            Protocols = ServiceProtocols.All,
            SpatialReference = MetadataV2SpatialReference.WebMercator,
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = publishedAt,
            },
        };
        var currentService = persistedService with
        {
            Metadata = persistedService.Metadata with { Publisher = "Concurrent Data Office" },
            Route = "/concurrent/route",
            Protocols = [.. persistedService.Protocols, "ConcurrentProtocol"],
            Status = persistedService.Status with { Conditions = [concurrentCondition] },
        };
        var previous = new MetadataV2Graph { Revision = 7, Services = [previousService] };
        var persisted = previous with { Revision = 8, Services = [persistedService] };
        var current = persisted with { Revision = 9, Services = [currentService] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var service = compensation.Services.Should().ContainSingle().Which;
        service.ServiceType.Should().Be(previousService.ServiceType);
        service.Route.Should().Be("/concurrent/route", "a later edit to the same field wins");
        service.SpatialReference.Should().Be(previousService.SpatialReference);
        service.Protocols.Should().Equal(ServiceProtocols.OgcFeatures, "ConcurrentProtocol");
        service.Status.Lifecycle.Should().Be(previousService.Status.Lifecycle);
        service.Status.State.Should().Be(previousService.Status.State);
        service.Status.ObservedAt.Should().Be(previousService.Status.ObservedAt);
        service.Status.Conditions.Should().Equal(originalCondition, concurrentCondition);
        service.Metadata.Title.Should().BeNull();
        service.Metadata.UpdatedAt.Should().Be(originalAt);
        service.Metadata.Publisher.Should().Be("Concurrent Data Office");
    }

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
    public void IndexSourceGovernanceByStorageLayer_WithDisabledSameNameFeatureService_UsesEnabledService()
    {
        var disabledMetadata = new MetadataV2ObjectMetadata { Id = "resource-disabled", License = "MIT" };
        var enabledMetadata = new MetadataV2ObjectMetadata { Id = "resource-enabled", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-disabled", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.OgcFeatures]
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-enabled", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer]
                }
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = disabledMetadata },
                new MetadataV2Resource { Metadata = enabledMetadata }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-disabled" },
                    ResourceId = "resource-disabled",
                    StorageLayerId = 7
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-enabled" },
                    ResourceId = "resource-enabled",
                    StorageLayerId = 8
                }
            ],
            Publications =
            [
                CreatePublication("pub-disabled", "service-disabled", "resource-disabled", "binding-disabled", 0, MetadataV2PublicationType.EsriFeatureLayer),
                CreatePublication("pub-enabled", "service-enabled", "resource-enabled", "binding-enabled", 0, MetadataV2PublicationType.EsriFeatureLayer)
            ]
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(8, enabledMetadata));
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithDisabledExactIdCollision_UsesEnabledService()
    {
        var disabledMetadata = new MetadataV2ObjectMetadata { Id = "resource-disabled", License = "MIT" };
        var enabledMetadata = new MetadataV2ObjectMetadata { Id = "resource-enabled", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "shared", Name = "disabled-name" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-enabled", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                },
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = disabledMetadata },
                new MetadataV2Resource { Metadata = enabledMetadata },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-disabled" },
                    ResourceId = "resource-disabled",
                    StorageLayerId = 7,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-enabled" },
                    ResourceId = "resource-enabled",
                    StorageLayerId = 8,
                },
            ],
            Publications =
            [
                CreatePublication("pub-disabled", "shared", "resource-disabled", "binding-disabled", 0, MetadataV2PublicationType.EsriFeatureLayer),
                CreatePublication("pub-enabled", "service-enabled", "resource-enabled", "binding-enabled", 0, MetadataV2PublicationType.EsriFeatureLayer),
            ],
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(8, enabledMetadata));
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
    public void BuildLinkedLayerMetadataV2Graph_WithMissingCanonicalBinding_ReturnsConflict()
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
            ]
        };

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Missing layer",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.LayerId.Should().Be(101);
        exception.Message.Should().Contain("absent from the canonical metadata graph");
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithMissingCanonicalResource_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-101" },
                    ResourceId = "resource-missing",
                    StorageLayerId = 101
                }
            ]
        };

        var action = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Dangling layer",
            4326,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z", CultureInfo.InvariantCulture));

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.LayerId.Should().Be(101);
        exception.Message.Should().Contain("references missing canonical resource 'resource-missing'");
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
