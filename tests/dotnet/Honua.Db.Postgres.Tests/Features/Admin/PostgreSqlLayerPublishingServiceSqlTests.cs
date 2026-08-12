// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
    public async Task PersistMetadataV2MutationAsync_WithUnknownGraphCommit_CompensatesUsingReceipt()
    {
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-original" },
                },
            ],
        };
        var updated = previous with
        {
            Revision = 8,
            Resources =
            [
                previous.Resources[0] with
                {
                    Metadata = previous.Resources[0].Metadata with { Title = "Failed update" },
                },
            ],
        };
        var store = new IndeterminateCommitGraphStore(updated);
        var service = new PostgreSqlLayerPublishingService(
            Mock.Of<ITableDiscoveryService>(),
            store,
            NullLogger<PostgreSqlLayerPublishingService>.Instance);

        var action = () => service.PersistMetadataV2MutationAsync(
            previous,
            updated,
            "\"previous\"",
            CancellationToken.None);

        await action.Should().ThrowAsync<MetadataV2GraphCommitOutcomeUnknownException>();
        store.SavedGraphs.Should().HaveCount(2);
        store.SavedGraphs[1].Revision.Should().Be(9);
        store.SavedGraphs[1].Resources.Should().BeEquivalentTo(previous.Resources);
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
        var coupledConcurrentPublication = CreatePublication(
            "pub-concurrent-coupled",
            "service-shared",
            "resource-failed",
            "binding-failed",
            4,
            MetadataV2PublicationType.EsriFeatureLayer);
        var current = persisted with
        {
            Revision = 9,
            Services =
            [
                persisted.Services[0] with
                {
                    PublicationIds =
                    [
                        "pub-original",
                        "pub-failed",
                        "pub-concurrent",
                        "pub-concurrent-coupled",
                    ],
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
            Publications =
            [
                persisted.Publications[0],
                persisted.Publications[1] with
                {
                    Metadata = persisted.Publications[1].Metadata with { Title = "Concurrent title" },
                },
                concurrentPublication,
                coupledConcurrentPublication,
            ],
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
    public void BuildRebasedCompensatingMetadataV2Graph_DerivesResourceLifecycleFromRebasedBindings()
    {
        var retiredStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-shared" },
                    StorageBindingIds = ["binding-target", "binding-sibling"],
                    PrimaryStorageBindingId = "binding-target",
                    Status = retiredStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-target" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 7,
                    Status = retiredStatus,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-sibling" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 8,
                    Status = retiredStatus,
                },
            ],
        };
        var persisted = previous with
        {
            Revision = 8,
            Resources = [previous.Resources[0] with { Status = activeStatus }],
            StorageBindings =
            [
                previous.StorageBindings[0] with { Status = activeStatus },
                previous.StorageBindings[1],
            ],
        };
        var current = persisted with
        {
            Revision = 9,
            StorageBindings =
            [
                persisted.StorageBindings[0],
                persisted.StorageBindings[1] with { Status = activeStatus },
            ],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T20:00:00Z", CultureInfo.InvariantCulture));

        compensation.StorageBindings.Single(binding => binding.Metadata.Id == "binding-target")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
        compensation.StorageBindings.Single(binding => binding.Metadata.Id == "binding-sibling")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        compensation.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);
    }

    [Theory]
    [InlineData("resource-concurrent", "binding-failed", true)]
    [InlineData("resource-failed", "binding-concurrent", true)]
    [InlineData("resource-concurrent", "binding-concurrent", false)]
    public void BuildRebasedCompensatingMetadataV2Graph_RemovesAddedPublicationUntilTargetIsFullyRepurposed(
        string currentResourceId,
        string currentBindingId,
        bool expectedRemoval)
    {
        var failedPublication = CreatePublication(
            "pub-failed",
            "service-shared",
            "resource-failed",
            "binding-failed",
            2,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previous = new MetadataV2Graph { Revision = 7 };
        var persisted = previous with { Revision = 8, Publications = [failedPublication] };
        var currentPublication = failedPublication with
        {
            ResourceId = currentResourceId,
            StorageBindingId = currentBindingId,
        };
        var current = persisted with { Revision = 9, Publications = [currentPublication] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:30:00Z", CultureInfo.InvariantCulture));

        if (expectedRemoval)
        {
            compensation.Publications.Should().BeEmpty();
        }
        else
        {
            compensation.Publications.Should().ContainSingle().Which.Should().Be(currentPublication);
        }
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_PreservesRepurposedAddedService()
    {
        var failedPublication = CreatePublication(
            "pub-failed",
            "service-added",
            "resource-failed",
            "binding-failed",
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-added", Name = "failed-service" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = "/rest/services/failed/FeatureServer",
            Protocols = ServiceProtocols.All,
            PublicationIds = [failedPublication.Metadata.Id],
        };
        var previous = new MetadataV2Graph { Revision = 7 };
        var persisted = previous with
        {
            Revision = 8,
            Services = [failedService],
            Publications = [failedPublication],
        };
        var repurposedService = failedService with
        {
            Metadata = failedService.Metadata with { Name = "concurrent-ogc-service" },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = "/ogc/features",
            Protocols = [ServiceProtocols.OgcFeatures],
            PublicationIds = [],
        };
        var current = persisted with
        {
            Revision = 9,
            Services = [repurposedService],
            Publications = [],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:45:00Z", CultureInfo.InvariantCulture));

        var retainedService = compensation.Services.Should().ContainSingle().Which;
        retainedService.Metadata.Name.Should().Be("concurrent-ogc-service");
        retainedService.ServiceType.Should().Be(MetadataV2ServiceType.OgcApiFeatures);
        retainedService.Route.Should().Be("/ogc/features");
        retainedService.Protocols.Should().Equal(ServiceProtocols.OgcFeatures);
        retainedService.PublicationIds.Should().BeEmpty();
        compensation.Publications.Should().BeEmpty();
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_PreservesRepurposedAddedResource()
    {
        var failedPublication = CreatePublication(
            "pub-failed",
            "service-existing",
            "resource-added",
            "binding-added",
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previousService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = ServiceProtocols.All,
        };
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added", Name = "failed-resource" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            Locator = "failed_table",
            StorageLayerId = 0,
        };
        var previous = new MetadataV2Graph { Revision = 7, Services = [previousService] };
        var persisted = previous with
        {
            Revision = 8,
            Services = [previousService with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        var repurposedResource = failedResource with
        {
            Metadata = failedResource.Metadata with { Name = "concurrent-document" },
            Type = MetadataV2ResourceType.Document,
            StorageBindingIds = [],
            PrimaryStorageBindingId = null,
        };
        var repurposedPublication = failedPublication with
        {
            Metadata = failedPublication.Metadata with { Id = "pub-concurrent-document" },
            StorageBindingId = null,
            PublicationType = MetadataV2PublicationType.OgcCollection,
        };
        var current = persisted with
        {
            Revision = 9,
            Services = [previousService with { PublicationIds = [repurposedPublication.Metadata.Id] }],
            Resources = [repurposedResource],
            StorageBindings = [],
            Publications = [repurposedPublication],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:50:00Z", CultureInfo.InvariantCulture));

        var retainedResource = compensation.Resources.Should().ContainSingle().Which;
        retainedResource.Metadata.Name.Should().Be("concurrent-document");
        retainedResource.Type.Should().Be(MetadataV2ResourceType.Document);
        retainedResource.StorageBindingIds.Should().BeEmpty();
        retainedResource.PrimaryStorageBindingId.Should().BeNull();
        compensation.StorageBindings.Should().BeEmpty();
        compensation.Publications.Should().ContainSingle().Which.Should().Be(repurposedPublication);
        compensation.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal(repurposedPublication.Metadata.Id);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RemovesIncidentallyStyledFailedResource()
    {
        var styleResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "style-existing" },
            Type = MetadataV2ResourceType.Style,
        };
        var previousService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = ServiceProtocols.All,
        };
        var failedPublication = CreatePublication(
            "pub-failed",
            previousService.Metadata.Id,
            "resource-added",
            "binding-added",
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            Locator = "failed_table",
            StorageLayerId = 0,
        };
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            Services = [previousService],
            Resources = [styleResource],
        };
        var persisted = previous with
        {
            Revision = 8,
            Services = [previousService with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [styleResource, failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        var concurrentlyStyledResource = failedResource with
        {
            StyleResourceIds = [styleResource.Metadata.Id],
        };
        var current = persisted with
        {
            Revision = 9,
            Resources = [styleResource, concurrentlyStyledResource],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:52:00Z", CultureInfo.InvariantCulture));

        compensation.Resources.Should().ContainSingle().Which.Metadata.Id.Should().Be(styleResource.Metadata.Id);
        compensation.StorageBindings.Should().BeEmpty();
        compensation.Publications.Should().BeEmpty();
        compensation.Services.Should().ContainSingle().Which.PublicationIds.Should().BeEmpty();
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_PreservesRepurposedAddedBindingAndConnection()
    {
        var existingResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-existing" },
            Type = MetadataV2ResourceType.Document,
        };
        var failedConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "connection-added" },
            Type = MetadataV2ConnectionType.Database,
            Provider = "postgres",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = existingResource.Metadata.Id,
            ConnectionId = failedConnection.Metadata.Id,
            Locator = "failed_table",
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [existingResource] };
        var persisted = previous with
        {
            Revision = 8,
            StorageBindings = [failedBinding],
            Connections = [failedConnection],
        };
        var repurposedBinding = failedBinding with
        {
            ConnectionId = null,
            StorageType = MetadataV2StorageType.ExternalApi,
            Locator = "https://example.test/concurrent-resource",
        };
        var repurposedConnection = failedConnection with
        {
            Type = MetadataV2ConnectionType.HttpApi,
            Provider = "concurrent-http",
            Endpoint = new Uri("https://example.test/api"),
        };
        var current = persisted with
        {
            Revision = 9,
            StorageBindings = [repurposedBinding],
            Connections = [repurposedConnection],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:55:00Z", CultureInfo.InvariantCulture));

        var retainedBinding = compensation.StorageBindings.Should().ContainSingle().Which;
        retainedBinding.ConnectionId.Should().BeNull();
        retainedBinding.StorageType.Should().Be(MetadataV2StorageType.ExternalApi);
        retainedBinding.Locator.Should().Be("https://example.test/concurrent-resource");
        var retainedConnection = compensation.Connections.Should().ContainSingle().Which;
        retainedConnection.Type.Should().Be(MetadataV2ConnectionType.HttpApi);
        retainedConnection.Provider.Should().Be("concurrent-http");
        retainedConnection.Endpoint.Should().Be(new Uri("https://example.test/api"));
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_PreservesPublicationOverRepurposedBinding()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
        };
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "failed_table",
        };
        var failedPublication = CreatePublication(
            "publication-failed",
            service.Metadata.Id,
            failedResource.Metadata.Id,
            failedBinding.Metadata.Id,
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previous = new MetadataV2Graph { Revision = 7, Services = [service] };
        var persisted = previous with
        {
            Revision = 8,
            Services = [service with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        var repurposedBinding = failedBinding with
        {
            StorageType = MetadataV2StorageType.ExternalApi,
            Locator = "https://example.test/concurrent-resource",
        };
        var concurrentPublication = failedPublication with
        {
            Metadata = failedPublication.Metadata with { Id = "publication-concurrent" },
        };
        var current = persisted with
        {
            Revision = 9,
            Services = [service with { PublicationIds = [concurrentPublication.Metadata.Id] }],
            StorageBindings = [repurposedBinding],
            Publications = [concurrentPublication],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:57:00Z", CultureInfo.InvariantCulture));

        compensation.Publications.Should().ContainSingle().Which.Should().Be(concurrentPublication);
        compensation.Resources.Should().ContainSingle().Which.Should().Be(failedResource);
        compensation.StorageBindings.Should().ContainSingle().Which.Should().Be(repurposedBinding);
        compensation.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal(concurrentPublication.Metadata.Id);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_PreservesAddedPublicationIdOverRepurposedBinding()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
        };
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "failed_table",
        };
        var failedPublication = CreatePublication(
            "publication-failed",
            service.Metadata.Id,
            failedResource.Metadata.Id,
            failedBinding.Metadata.Id,
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previous = new MetadataV2Graph { Revision = 7, Services = [service] };
        var persisted = previous with
        {
            Revision = 8,
            Services = [service with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        var repurposedBinding = failedBinding with
        {
            StorageType = MetadataV2StorageType.ExternalApi,
            Locator = "https://example.test/concurrent-resource",
        };
        var current = persisted with
        {
            Revision = 9,
            StorageBindings = [repurposedBinding],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:58:00Z", CultureInfo.InvariantCulture));

        compensation.Publications.Should().ContainSingle().Which.Should().Be(failedPublication);
        compensation.Resources.Should().ContainSingle().Which.Should().Be(failedResource);
        compensation.StorageBindings.Should().ContainSingle().Which.Should().Be(repurposedBinding);
        compensation.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal(failedPublication.Metadata.Id);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_DoesNotTreatBindingOwnershipAsTargetRepurposing()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Protocols = [ServiceProtocols.FeatureServer],
        };
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "failed_table",
            StorageLayerId = 42,
        };
        var failedPublication = CreatePublication(
            "publication-failed",
            service.Metadata.Id,
            failedResource.Metadata.Id,
            failedBinding.Metadata.Id,
            0,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previous = new MetadataV2Graph { Revision = 7, Services = [service] };
        var persisted = previous with
        {
            Revision = 8,
            Services = [service with { PublicationIds = [failedPublication.Metadata.Id] }],
            Resources = [failedResource],
            StorageBindings = [failedBinding],
            Publications = [failedPublication],
        };
        var concurrentResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-concurrent" },
            StorageBindingIds = [failedBinding.Metadata.Id],
            PrimaryStorageBindingId = failedBinding.Metadata.Id,
        };
        var movedBinding = failedBinding with { ResourceId = concurrentResource.Metadata.Id };
        var movedPublication = failedPublication with { ResourceId = concurrentResource.Metadata.Id };
        var current = persisted with
        {
            Revision = 9,
            Resources =
            [
                failedResource with { StorageBindingIds = [], PrimaryStorageBindingId = null },
                concurrentResource,
            ],
            StorageBindings = [movedBinding],
            Publications = [movedPublication],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T04:59:00Z", CultureInfo.InvariantCulture));

        compensation.Publications.Should().BeEmpty();
        compensation.StorageBindings.Should().BeEmpty();
        var retainedResource = compensation.Resources.Should().ContainSingle().Which;
        retainedResource.Should().BeEquivalentTo(concurrentResource with
        {
            StorageBindingIds = [],
            PrimaryStorageBindingId = null,
        });
        compensation.Services.Should().ContainSingle().Which.PublicationIds.Should().BeEmpty();
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RepairsExistingPublicationBeforeRemovingFailedBinding()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            PublicationIds = ["publication-existing"],
        };
        var existingResource = new MetadataV2Resource
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
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "failed_table",
            StorageLayerId = 42,
        };
        var previousTargets = new[]
        {
            (Resource: existingResource, Publication: existingPublication),
            (
                Resource: existingResource with { StorageBindingIds = [], PrimaryStorageBindingId = null },
                Publication: existingPublication with { StorageBindingId = null }),
        };
        foreach (var previousTarget in previousTargets)
        {
            var previous = new MetadataV2Graph
            {
                Revision = 7,
                Services = [service],
                Resources = [previousTarget.Resource],
                StorageBindings = [existingBinding],
                Publications = [previousTarget.Publication],
            };
            var persisted = previous with
            {
                Revision = 8,
                Resources = [previousTarget.Resource, failedResource],
                StorageBindings = [existingBinding, failedBinding],
            };
            var currentResource = previousTarget.Resource with
            {
                StorageBindingIds = [.. previousTarget.Resource.StorageBindingIds, failedBinding.Metadata.Id],
                PrimaryStorageBindingId = failedBinding.Metadata.Id,
            };
            var movedBinding = failedBinding with { ResourceId = existingResource.Metadata.Id };
            var movedPublication = previousTarget.Publication with
            {
                Metadata = previousTarget.Publication.Metadata with { Title = "Concurrent title" },
                StorageBindingId = failedBinding.Metadata.Id,
            };
            var current = persisted with
            {
                Revision = 9,
                Resources =
                [
                    currentResource,
                    failedResource with { StorageBindingIds = [], PrimaryStorageBindingId = null },
                ],
                StorageBindings = [existingBinding, movedBinding],
                Publications = [movedPublication],
            };

            var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
                current,
                previous,
                persisted,
                DateTimeOffset.Parse("2026-08-11T05:01:00Z", CultureInfo.InvariantCulture));

            var repairedPublication = compensation.Publications.Should().ContainSingle().Which;
            repairedPublication.Metadata.Title.Should().Be("Concurrent title");
            repairedPublication.ResourceId.Should().Be(existingResource.Metadata.Id);
            repairedPublication.StorageBindingId.Should().Be(previousTarget.Publication.StorageBindingId);
            compensation.StorageBindings.Should().ContainSingle().Which.Should().BeEquivalentTo(existingBinding);
            var repairedResource = compensation.Resources.Should().ContainSingle().Which;
            repairedResource.StorageBindingIds.Should().Equal(previousTarget.Resource.StorageBindingIds);
            var expectedPrimaryBindingId = previousTarget.Resource.StorageBindingIds.Count > 0
                ? previousTarget.Resource.StorageBindingIds[0]
                : null;
            repairedResource.PrimaryStorageBindingId.Should()
                .Be(expectedPrimaryBindingId);
            compensation.Services.Should().ContainSingle().Which.PublicationIds
                .Should().Equal(existingPublication.Metadata.Id);
        }
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RemovesExistingPublicationWhenOriginalTargetWasDeleted()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-existing" },
            PublicationIds = ["publication-existing"],
        };
        var existingResource = new MetadataV2Resource
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
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-added" },
            StorageBindingIds = ["binding-added"],
            PrimaryStorageBindingId = "binding-added",
        };
        var failedBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-added" },
            ResourceId = failedResource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "failed_table",
            StorageLayerId = 42,
        };
        var previousTargets = new[]
        {
            (Resource: existingResource, Publication: existingPublication),
            (Resource: existingResource, Publication: existingPublication with { StorageBindingId = null }),
            (
                Resource: existingResource with { StorageBindingIds = [], PrimaryStorageBindingId = null },
                Publication: existingPublication with { StorageBindingId = null }),
        };
        foreach (var previousTarget in previousTargets)
        {
            var previous = new MetadataV2Graph
            {
                Revision = 7,
                Services = [service],
                Resources = [previousTarget.Resource],
                StorageBindings = [existingBinding],
                Publications = [previousTarget.Publication],
            };
            var persisted = previous with
            {
                Revision = 8,
                Resources = [previousTarget.Resource, failedResource],
                StorageBindings = [existingBinding, failedBinding],
            };

            foreach (var deleteOriginalResource in new[] { true, false })
            {
                var movedBinding = deleteOriginalResource
                    ? failedBinding
                    : failedBinding with { ResourceId = existingResource.Metadata.Id };
                var movedPublication = previousTarget.Publication with
                {
                    ResourceId = movedBinding.ResourceId,
                    StorageBindingId = movedBinding.Metadata.Id,
                };
                var current = persisted with
                {
                    Revision = 9,
                    Resources = deleteOriginalResource
                        ? [failedResource]
                        :
                        [
                            previousTarget.Resource with
                            {
                                StorageBindingIds = [movedBinding.Metadata.Id],
                                PrimaryStorageBindingId = movedBinding.Metadata.Id,
                            },
                        ],
                    StorageBindings = [movedBinding],
                    Publications = [movedPublication],
                };

                var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
                    current,
                    previous,
                    persisted,
                    DateTimeOffset.Parse("2026-08-11T05:02:00Z", CultureInfo.InvariantCulture));

                compensation.Publications.Should().BeEmpty();
                compensation.StorageBindings.Should().BeEmpty();
                compensation.Services.Should().ContainSingle().Which.PublicationIds.Should().BeEmpty();
                if (deleteOriginalResource)
                {
                    compensation.Resources.Should().BeEmpty();
                }
                else
                {
                    var retainedResource = compensation.Resources.Should().ContainSingle().Which;
                    retainedResource.Metadata.Id.Should().Be(existingResource.Metadata.Id);
                    retainedResource.StorageBindingIds.Should().BeEmpty();
                    retainedResource.PrimaryStorageBindingId.Should().BeNull();
                }
            }
        }
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresPublicationReplacedByFailedUpsert()
    {
        var previousPublication = CreatePublication(
            "pub-imported",
            "service-shared",
            "resource-existing",
            "binding-existing",
            1,
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedPublication = CreatePublication(
            "pub-generated",
            "service-shared",
            "resource-existing",
            "binding-existing",
            1,
            MetadataV2PublicationType.EsriFeatureLayer);
        var concurrentPublication = CreatePublication(
            "pub-concurrent",
            "service-shared",
            "resource-concurrent",
            "binding-concurrent",
            2,
            MetadataV2PublicationType.EsriFeatureLayer);
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-shared" },
                    PublicationIds = [previousPublication.Metadata.Id],
                },
            ],
            Publications = [previousPublication],
        };
        var persisted = previous with
        {
            Revision = 8,
            Services =
            [
                previous.Services[0] with
                {
                    PublicationIds = [previousPublication.Metadata.Id, failedPublication.Metadata.Id],
                },
            ],
            Publications = [failedPublication],
        };
        var current = persisted with
        {
            Revision = 9,
            Services =
            [
                persisted.Services[0] with
                {
                    PublicationIds =
                    [
                        previousPublication.Metadata.Id,
                        failedPublication.Metadata.Id,
                        concurrentPublication.Metadata.Id,
                    ],
                },
            ],
            Publications = [failedPublication, concurrentPublication],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T05:00:00Z", CultureInfo.InvariantCulture));

        compensation.Publications.Should().Equal(previousPublication, concurrentPublication);
        compensation.Services.Should().ContainSingle().Which.PublicationIds
            .Should().Equal(previousPublication.Metadata.Id, concurrentPublication.Metadata.Id);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresReplacementDespiteConcurrentNonTargetEdit()
    {
        var previousPublication = CreatePublication(
            "pub-imported",
            "service-shared",
            "resource-existing",
            "binding-existing",
            1,
            MetadataV2PublicationType.EsriFeatureLayer);
        var failedPublication = CreatePublication(
            "pub-generated",
            "service-shared",
            "resource-existing",
            "binding-existing",
            1,
            MetadataV2PublicationType.EsriFeatureLayer);
        var concurrentPublication = failedPublication with
        {
            Metadata = failedPublication.Metadata with { Title = "Concurrent route title" },
        };
        var previous = new MetadataV2Graph { Revision = 7, Publications = [previousPublication] };
        var persisted = previous with { Revision = 8, Publications = [failedPublication] };
        var current = persisted with { Revision = 9, Publications = [concurrentPublication] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-11T05:30:00Z", CultureInfo.InvariantCulture));

        compensation.Publications.Should().ContainSingle().Which.Should().Be(previousPublication);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresExistingPublicationFieldsThreeWay()
    {
        var originalAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);
        var publishedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);
        var previousPublication = CreatePublication(
            "pub-existing",
            "service-original",
            "resource-original",
            "binding-original",
            1,
            MetadataV2PublicationType.EsriFeatureLayer) with
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "pub-existing",
                Title = "Original title",
                UpdatedAt = originalAt,
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Draft,
                State = MetadataV2OperationalState.Unknown,
                ObservedAt = originalAt,
            },
        };
        var persistedPublication = previousPublication with
        {
            Metadata = previousPublication.Metadata with
            {
                Title = "Published title",
                UpdatedAt = publishedAt,
            },
            ResourceId = "resource-published",
            StorageBindingId = "binding-published",
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = publishedAt,
            },
        };
        var currentPublication = persistedPublication with
        {
            Metadata = persistedPublication.Metadata with { Title = "Concurrent title" },
        };
        var previous = new MetadataV2Graph { Revision = 7, Publications = [previousPublication] };
        var persisted = previous with { Revision = 8, Publications = [persistedPublication] };
        var current = persisted with { Revision = 9, Publications = [currentPublication] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var publication = compensation.Publications.Should().ContainSingle().Which;
        publication.Metadata.Title.Should().Be("Concurrent title", "a later edit to the same field wins");
        publication.Metadata.UpdatedAt.Should().Be(originalAt);
        publication.ResourceId.Should().Be(previousPublication.ResourceId);
        publication.StorageBindingId.Should().Be(previousPublication.StorageBindingId);
        publication.Status.Should().Be(previousPublication.Status);
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
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresExistingResourceFieldsThreeWay()
    {
        var originalAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);
        var publishedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);
        var previousField = new MetadataV2Field { Name = "legacy_id", Type = MetadataV2FieldType.BigInteger };
        var persistedField = new MetadataV2Field { Name = "published_id", Type = MetadataV2FieldType.Integer };
        var previousLink = new MetadataV2Link
        {
            Href = "https://example.test/original",
            Rel = "describedby",
            Title = "Original title",
            ManagedBy = LayerSourceGovernance.LinkManager,
        };
        var persistedLink = previousLink with
        {
            Href = "https://example.test/failed",
            Title = "Failed title",
        };
        var concurrentlyEditedFailedLink = persistedLink with
        {
            Rel = "alternate",
            Title = "Concurrent title",
            ManagedBy = "concurrent-writer",
        };
        var concurrentLink = new MetadataV2Link { Href = "https://example.test/concurrent", Rel = "related" };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource-layer-7",
                Name = "Imported resource",
                Publisher = "Original publisher",
                ContactPoint = new MetadataV2ContactPoint
                {
                    Name = "Original contact",
                    Email = "original@example.test",
                    Url = "https://example.test/original-contact",
                },
                CreatedAt = originalAt,
                UpdatedAt = originalAt,
                Tags = ["tag-original"],
                Labels = new Dictionary<string, string>
                {
                    ["restored"] = "original",
                    ["edited"] = "original",
                },
                Annotations = new Dictionary<string, string> { ["restored"] = "original" },
                Keywords = ["keyword-original"],
                Themes = ["theme-original"],
                Links = [previousLink],
            },
            StorageBindingIds = ["binding-imported"],
            PrimaryStorageBindingId = "binding-imported",
            SchemaFields = [previousField],
            PolicyIds = ["policy-original"],
            StyleResourceIds = ["style-original"],
            AccessPolicy = new AccessPolicy
            {
                AllowAnonymous = false,
                AllowAnonymousWrite = false,
                AllowedRoles = ["reader"],
                AllowedWriteRoles = ["editor"],
            },
            Temporal = new MetadataV2ResourceTemporal
            {
                StartTimeField = "started_at",
                EndTimeField = "ended_at",
                Extent = new MetadataV2TimeRange
                {
                    Start = DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                    End = DateTimeOffset.Parse("2025-12-31T00:00:00Z", CultureInfo.InvariantCulture),
                },
            },
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                Bbox = new MetadataV2Bbox { West = 1, South = 2, East = 3, North = 4 },
                PrimaryGeometryField = "legacy_shape",
                StorageCrs = MetadataV2SpatialReference.Wgs84,
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Draft,
                State = MetadataV2OperationalState.Unknown,
                ObservedAt = originalAt,
            },
        };
        var persistedResource = previousResource with
        {
            Metadata = previousResource.Metadata with
            {
                Name = "Published resource",
                Publisher = null,
                ContactPoint = null,
                UpdatedAt = publishedAt,
                Tags = [],
                Labels = new Dictionary<string, string> { ["edited"] = "failed" },
                Annotations = new Dictionary<string, string>(),
                Keywords = [],
                Themes = [],
                Links = [persistedLink],
            },
            StorageBindingIds = ["binding-generated"],
            PrimaryStorageBindingId = "binding-generated",
            SchemaFields = [persistedField],
            PolicyIds = ["policy-failed"],
            StyleResourceIds = ["style-failed"],
            AccessPolicy = null,
            Temporal = null,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.Polygon,
                Bbox = new MetadataV2Bbox { West = 10, South = 20, East = 30, North = 40 },
                PrimaryGeometryField = "published_shape",
                StorageCrs = MetadataV2SpatialReference.WebMercator,
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = publishedAt,
            },
        };
        var concurrentBbox = new MetadataV2Bbox { West = 100, South = 200, East = 300, North = 400 };
        var currentResource = persistedResource with
        {
            Metadata = persistedResource.Metadata with
            {
                Publisher = "Concurrent Data Office",
                ContactPoint = new MetadataV2ContactPoint { Email = "concurrent@example.test" },
                Tags = ["tag-concurrent"],
                Labels = new Dictionary<string, string>
                {
                    ["edited"] = "concurrent",
                    ["concurrent"] = "added",
                },
                Annotations = new Dictionary<string, string> { ["concurrent"] = "added" },
                Keywords = ["keyword-concurrent"],
                Themes = ["theme-concurrent"],
                Links = [concurrentLink, concurrentlyEditedFailedLink],
            },
            StorageBindingIds = ["binding-generated", "binding-concurrent"],
            PolicyIds = ["policy-failed", "policy-concurrent"],
            StyleResourceIds = ["style-concurrent", "style-failed"],
            AccessPolicy = new AccessPolicy { AllowedWriteRoles = ["concurrent-writer"] },
            Temporal = new MetadataV2ResourceTemporal { TrackIdField = "concurrent_track" },
            Spatial = persistedResource.Spatial! with { Bbox = concurrentBbox },
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var resource = compensation.Resources.Should().ContainSingle().Which;
        resource.Metadata.Name.Should().Be(previousResource.Metadata.Name);
        resource.Metadata.UpdatedAt.Should().Be(originalAt);
        resource.Metadata.Publisher.Should().Be("Concurrent Data Office");
        resource.Metadata.ContactPoint.Should().BeEquivalentTo(new MetadataV2ContactPoint
        {
            Name = "Original contact",
            Email = "concurrent@example.test",
            Url = "https://example.test/original-contact",
        });
        resource.Metadata.Tags.Should().Equal("tag-original", "tag-concurrent");
        resource.Metadata.Labels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["restored"] = "original",
            ["edited"] = "concurrent",
            ["concurrent"] = "added",
        });
        resource.Metadata.Annotations.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["restored"] = "original",
            ["concurrent"] = "added",
        });
        resource.Metadata.Keywords.Should().Equal("keyword-original", "keyword-concurrent");
        resource.Metadata.Themes.Should().Equal("theme-original", "theme-concurrent");
        resource.Metadata.Links.Should().Equal(
            concurrentLink,
            previousLink with
            {
                Rel = "alternate",
                Title = "Concurrent title",
                ManagedBy = "concurrent-writer",
            });
        resource.StorageBindingIds.Should().Equal("binding-imported", "binding-concurrent");
        resource.PrimaryStorageBindingId.Should().Be("binding-imported");
        resource.SchemaFields.Should().Equal(previousField);
        resource.PolicyIds.Should().Equal("policy-original", "policy-concurrent");
        resource.StyleResourceIds.Should().Equal("style-concurrent", "style-original");
        resource.AccessPolicy!.AllowAnonymous.Should().BeFalse();
        resource.AccessPolicy.AllowAnonymousWrite.Should().BeFalse();
        resource.AccessPolicy.AllowedRoles.Should().Equal("reader");
        resource.AccessPolicy.AllowedWriteRoles.Should().Equal("concurrent-writer");
        resource.Temporal!.StartTimeField.Should().Be("started_at");
        resource.Temporal.EndTimeField.Should().Be("ended_at");
        resource.Temporal.TrackIdField.Should().Be("concurrent_track");
        resource.Temporal.Extent.Should().Be(previousResource.Temporal!.Extent);
        resource.Spatial!.SpatialReference.Should().Be(previousResource.Spatial!.SpatialReference);
        resource.Spatial.GeometryType.Should().Be(previousResource.Spatial.GeometryType);
        resource.Spatial.PrimaryGeometryField.Should().Be(previousResource.Spatial.PrimaryGeometryField);
        resource.Spatial.StorageCrs.Should().Be(previousResource.Spatial.StorageCrs);
        resource.Spatial.Bbox.Should().Be(concurrentBbox, "a later edit to the same field wins");
        resource.Status.Should().Be(previousResource.Status);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresAbsentSpatialMembersIndependently()
    {
        var persistedSpatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            Bbox = new MetadataV2Bbox { West = 1, South = 2, East = 3, North = 4 },
            PrimaryGeometryField = "failed_shape",
            SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            StorageCrs = MetadataV2SpatialReference.Wgs84,
            StorageCrsCoordinateEpoch = 2020,
        };
        var concurrentBbox = new MetadataV2Bbox { West = 10, South = 20, East = 30, North = 40 };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-spatial-null-baseline" },
            Spatial = null,
        };
        var persistedResource = previousResource with { Spatial = persistedSpatial };
        var currentResource = persistedResource with
        {
            Spatial = persistedSpatial with { Bbox = concurrentBbox },
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var spatial = compensation.Resources.Should().ContainSingle().Which.Spatial;
        spatial.Should().NotBeNull();
        spatial!.SpatialReference.Should().BeNull();
        spatial.GeometryType.Should().Be(MetadataV2GeometryType.None);
        spatial.Bbox.Should().Be(concurrentBbox);
        spatial.PrimaryGeometryField.Should().BeNull();
        spatial.SupportedCrs.Should().BeEmpty();
        spatial.StorageCrs.Should().BeNull();
        spatial.StorageCrsCoordinateEpoch.Should().BeNull();
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresAttributeRulesIndependently()
    {
        var previousChangedRule = new MetadataV2AttributeRule
        {
            Name = "changed-rule",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "true",
            TriggeringEvents = ["insert"],
            ErrorMessage = "Original message",
            IsEnabled = true,
            Batch = false,
        };
        var previousRemovedRule = new MetadataV2AttributeRule
        {
            Name = "removed-rule",
            Type = MetadataV2AttributeRuleType.Validation,
            ScriptExpression = "true",
        };
        var failedChangedRule = previousChangedRule with
        {
            Type = MetadataV2AttributeRuleType.Validation,
            ScriptExpression = "false",
            TriggeringEvents = ["update"],
            ErrorMessage = "Failed message",
            IsEnabled = false,
            Batch = true,
        };
        var failedAddedRule = new MetadataV2AttributeRule
        {
            Name = "failed-added-rule",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "false",
            TriggeringEvents = ["insert"],
        };
        var concurrentAddedRule = new MetadataV2AttributeRule
        {
            Name = "concurrent-added-rule",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "true",
        };
        var concurrentlyEditedRule = failedChangedRule with { ErrorMessage = "Concurrent message" };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-attribute-rules" },
            AttributeRules = [previousChangedRule, previousRemovedRule],
        };
        var persistedResource = previousResource with
        {
            AttributeRules = [failedChangedRule, failedAddedRule],
        };
        var currentResource = persistedResource with
        {
            AttributeRules =
            [
                concurrentAddedRule,
                concurrentlyEditedRule,
                failedAddedRule with { TriggeringEvents = ["insert"] },
            ],
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var rules = compensation.Resources.Should().ContainSingle().Which.AttributeRules;
        rules.Should().BeEquivalentTo(
            new[]
            {
                concurrentAddedRule,
                previousChangedRule with { ErrorMessage = "Concurrent message" },
                previousRemovedRule,
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresCompositeResourceMembersIndependently()
    {
        var previousRelationships = new[]
        {
            new MetadataV2Relationship
            {
                Id = "relationship-a",
                Name = "Original relationship",
                Description = "Original description",
                RelatedResourceId = "resource-related-a",
                Role = "origin",
                OriginField = "legacy_id",
                DestinationField = "legacy_parent_id",
            },
            new MetadataV2Relationship
            {
                Id = "relationship-b",
                Name = "Stable relationship",
                RelatedResourceId = "resource-related-b",
                Role = "destination",
                OriginField = "legacy_parent_id",
                DestinationField = "legacy_id",
            },
        };
        var failedRelationships = new[]
        {
            previousRelationships[0] with
            {
                Name = "Failed relationship",
                OriginField = "failed_id",
            },
            previousRelationships[1],
        };
        var concurrentRelationship = new MetadataV2Relationship
        {
            Id = "relationship-concurrent",
            Name = "Concurrent relationship",
            RelatedResourceId = "resource-related-c",
            Role = "origin",
            OriginField = "concurrent_id",
            DestinationField = "concurrent_parent_id",
        };
        var previousStyleEncoding = new MetadataV2StyleEncoding
        {
            Encoding = "mapbox-style",
            Body = "original-style",
            ContentType = "application/json",
        };
        var failedStyleEncoding = previousStyleEncoding with { Body = "failed-style" };
        var previousRule = new Symbology3DRule
        {
            Attribute = "category",
            Comparison = Symbology3DComparison.Equals,
            Value = "original",
            Opacity = 0.5,
            Visible = true,
        };
        var failedRule = previousRule with { Value = "failed", Visible = false };
        var previousSubtype = new MetadataV2Subtype
        {
            Code = JsonSerializer.SerializeToElement(1),
            Name = "Original subtype",
        };
        var failedSubtype = previousSubtype with { Name = "Failed subtype" };
        var previousContingentValue = new MetadataV2ContingentValue
        {
            Id = 1,
            SubtypeCode = JsonSerializer.SerializeToElement(1),
            Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = new MetadataV2ContingentFieldValue
                {
                    Type = "code",
                    Code = JsonSerializer.SerializeToElement("original"),
                },
            },
        };
        var failedContingentValue = previousContingentValue with
        {
            SubtypeCode = JsonSerializer.SerializeToElement(2),
            Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = new MetadataV2ContingentFieldValue
                {
                    Type = "code",
                    Code = JsonSerializer.SerializeToElement("failed"),
                },
            },
        };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-composites" },
            Relationships = previousRelationships,
            PermanentFilter = new MetadataV2PermanentFilter
            {
                Expression = "status = 'original'",
                Language = MetadataV2PermanentFilterLanguages.ArcGisSql,
            },
            Subtypes = new MetadataV2Subtypes
            {
                SubtypeField = "original_type",
                DefaultSubtypeCode = JsonSerializer.SerializeToElement(1),
                Subtypes = [previousSubtype],
            },
            ContingentValueGroups =
            [
                new MetadataV2ContingentValueGroup
                {
                    Name = "status-group",
                    Restrictive = true,
                    Fields = ["status", "category"],
                    ContingentValues = [previousContingentValue],
                },
            ],
            OwnerEditPolicy = new MetadataV2OwnerEditPolicy
            {
                Enabled = true,
                OwnerField = "original_owner",
                StampOwnerOnInsert = true,
            },
            Extrusion = new MetadataV2ExtrusionInfo
            {
                HeightField = "original_height",
                BaseHeightField = "original_base",
                Unit = MetadataV2VerticalUnits.Meters,
                DefaultHeight = 1,
                MaterialHint = "original-material",
            },
            Symbology3D = new Symbology3D
            {
                DefaultColor = new Symbology3DColor(1, 2, 3),
                DefaultOpacity = 0.25,
                Rules = [previousRule],
            },
            Style = new MetadataV2ResourceStyle
            {
                Title = "Original style",
                Abstract = "Original abstract",
                StyleVersion = 1,
                Encodings = [previousStyleEncoding],
            },
            Display = new MetadataV2ResourceDisplay
            {
                MinScale = 100,
                MaxScale = 1_000,
                DefaultVisibility = true,
                DisplayField = "original_name",
                Queryable = true,
                HasZ = false,
                HasM = false,
            },
            Editing = new MetadataV2ResourceEditing
            {
                GlobalIdField = "original_global_id",
                CreatorField = "original_creator",
                CreatedAtField = "original_created_at",
                EditorField = "original_editor",
                UpdatedAtField = "original_updated_at",
                CanModify = true,
                SupportsAttachments = false,
                SupportsRelatedRecords = true,
            },
        };
        var persistedResource = previousResource with
        {
            Relationships = failedRelationships,
            PermanentFilter = previousResource.PermanentFilter! with
            {
                Expression = "status = 'failed'",
                Language = MetadataV2PermanentFilterLanguages.Cql2Text,
            },
            Subtypes = previousResource.Subtypes! with
            {
                SubtypeField = "failed_type",
                DefaultSubtypeCode = JsonSerializer.SerializeToElement(2),
                Subtypes = [failedSubtype],
            },
            ContingentValueGroups =
            [
                previousResource.ContingentValueGroups[0] with
                {
                    Restrictive = false,
                    Fields = ["status"],
                    ContingentValues = [failedContingentValue],
                },
            ],
            OwnerEditPolicy = previousResource.OwnerEditPolicy! with
            {
                OwnerField = "failed_owner",
                StampOwnerOnInsert = false,
            },
            Extrusion = previousResource.Extrusion! with
            {
                HeightField = "failed_height",
                BaseHeightField = "failed_base",
                Unit = MetadataV2VerticalUnits.Feet,
                DefaultHeight = 2,
                MaterialHint = "failed-material",
            },
            Symbology3D = previousResource.Symbology3D! with
            {
                DefaultOpacity = 0.75,
                Rules = [failedRule],
            },
            Style = previousResource.Style! with
            {
                Title = "Failed style",
                Abstract = "Failed abstract",
                StyleVersion = 2,
                Encodings = [failedStyleEncoding],
            },
            Display = previousResource.Display! with
            {
                MinScale = 200,
                MaxScale = 2_000,
                DefaultVisibility = false,
                DisplayField = "failed_name",
                Queryable = false,
                HasZ = true,
                HasM = true,
            },
            Editing = previousResource.Editing! with
            {
                GlobalIdField = "failed_global_id",
                CreatorField = "failed_creator",
                CreatedAtField = "failed_created_at",
                EditorField = "failed_editor",
                UpdatedAtField = "failed_updated_at",
                CanModify = false,
                SupportsAttachments = true,
                SupportsRelatedRecords = false,
            },
        };
        var currentResource = persistedResource with
        {
            Relationships =
            [
                failedRelationships[0],
                failedRelationships[1] with { Description = "Concurrent description" },
                concurrentRelationship,
            ],
            PermanentFilter = persistedResource.PermanentFilter! with { Language = "concurrent-language" },
            Subtypes = persistedResource.Subtypes! with
            {
                Subtypes =
                [
                    failedSubtype with
                    {
                        FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["concurrent_field"] = new MetadataV2SubtypeFieldOverride
                            {
                                DefaultValue = JsonSerializer.SerializeToElement("concurrent"),
                            },
                        },
                    },
                ],
            },
            ContingentValueGroups =
            [
                persistedResource.ContingentValueGroups[0] with
                {
                    ContingentValues =
                    [
                        failedContingentValue with
                        {
                            Values = new Dictionary<string, MetadataV2ContingentFieldValue>(
                                failedContingentValue.Values,
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["concurrent_field"] = new MetadataV2ContingentFieldValue { Type = "any" },
                            },
                        },
                    ],
                },
            ],
            OwnerEditPolicy = persistedResource.OwnerEditPolicy! with { Enabled = false },
            Extrusion = persistedResource.Extrusion! with { MaterialHint = "concurrent-material" },
            Symbology3D = persistedResource.Symbology3D! with
            {
                DefaultColor = new Symbology3DColor(10, 20, 30),
                Rules = [failedRule with { Opacity = 0.9 }],
            },
            Style = persistedResource.Style! with
            {
                Abstract = "Concurrent abstract",
                Encodings = [failedStyleEncoding with { ContentType = "application/vnd.concurrent+json" }],
            },
            Display = persistedResource.Display! with { MinScale = 300 },
            Editing = persistedResource.Editing! with { CreatorField = "concurrent_creator" },
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var resource = compensation.Resources.Should().ContainSingle().Which;
        resource.Relationships.Should().BeEquivalentTo(
            new[]
            {
                previousRelationships[0],
                previousRelationships[1] with { Description = "Concurrent description" },
                concurrentRelationship,
            },
            options => options.WithStrictOrdering());
        resource.PermanentFilter.Should().Be(new MetadataV2PermanentFilter
        {
            Expression = "status = 'original'",
            Language = "concurrent-language",
        });
        resource.Subtypes!.SubtypeField.Should().Be("original_type");
        resource.Subtypes.DefaultSubtypeCode!.Value.GetInt32().Should().Be(1);
        var subtype = resource.Subtypes.Subtypes.Should().ContainSingle().Which;
        subtype.Name.Should().Be("Original subtype");
        subtype.FieldOverrides.Should().ContainSingle().Which.Key.Should().Be("concurrent_field");
        subtype.FieldOverrides["concurrent_field"].DefaultValue!.Value.GetString().Should().Be("concurrent");
        var contingentGroup = resource.ContingentValueGroups.Should().ContainSingle().Which;
        contingentGroup.Restrictive.Should().BeTrue();
        contingentGroup.Fields.Should().Equal("status", "category");
        var contingentValue = contingentGroup.ContingentValues.Should().ContainSingle().Which;
        contingentValue.SubtypeCode!.Value.GetInt32().Should().Be(1);
        contingentValue.Values["status"].Code!.Value.GetString().Should().Be("original");
        contingentValue.Values.Should().ContainKey("concurrent_field");
        resource.OwnerEditPolicy.Should().Be(previousResource.OwnerEditPolicy with { Enabled = false });
        resource.Extrusion.Should().Be(previousResource.Extrusion with { MaterialHint = "concurrent-material" });
        resource.Symbology3D!.DefaultColor.Should().Be(new Symbology3DColor(10, 20, 30));
        resource.Symbology3D.DefaultOpacity.Should().Be(0.25);
        resource.Symbology3D.Rules.Should().ContainSingle().Which.Should().Be(
            previousRule with { Opacity = 0.9 });
        resource.Style.Should().BeEquivalentTo(
            previousResource.Style! with
            {
                Abstract = "Concurrent abstract",
                Encodings =
                [
                    previousStyleEncoding with { ContentType = "application/vnd.concurrent+json" },
                ],
            });
        resource.Display.Should().Be(previousResource.Display with { MinScale = 300 });
        resource.Editing.Should().Be(previousResource.Editing with { CreatorField = "concurrent_creator" });
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RebasesArraysAcrossAbsentCompositeBaselines()
    {
        var previousSubtype = new MetadataV2Subtype
        {
            Code = JsonSerializer.SerializeToElement(1),
            Name = "Previous subtype",
        };
        var concurrentSubtype = new MetadataV2Subtype
        {
            Code = JsonSerializer.SerializeToElement(2),
            Name = "Concurrent subtype",
        };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-cleared-subtypes" },
            Subtypes = new MetadataV2Subtypes
            {
                SubtypeField = "previous_type",
                Subtypes = [previousSubtype],
            },
        };
        var clearedResource = previousResource with { Subtypes = null };
        var concurrentlyRecreatedResource = clearedResource with
        {
            Subtypes = new MetadataV2Subtypes
            {
                SubtypeField = "concurrent_type",
                Subtypes = [concurrentSubtype],
            },
        };

        var restoredClear = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            new MetadataV2Graph { Revision = 9, Resources = [concurrentlyRecreatedResource] },
            new MetadataV2Graph { Revision = 7, Resources = [previousResource] },
            new MetadataV2Graph { Revision = 8, Resources = [clearedResource] },
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var recreatedSubtypes = restoredClear.Resources.Should().ContainSingle().Which.Subtypes;
        recreatedSubtypes!.SubtypeField.Should().Be("concurrent_type");
        recreatedSubtypes.Subtypes.Select(subtype => subtype.Code.GetInt32()).Should().Equal(2, 1);

        var emptyResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-introduced-subtypes" },
        };
        var failedIntroducedResource = emptyResource with
        {
            Subtypes = new MetadataV2Subtypes
            {
                SubtypeField = "failed_type",
                Subtypes = [previousSubtype],
            },
        };
        var concurrentlyExtendedResource = failedIntroducedResource with
        {
            Subtypes = failedIntroducedResource.Subtypes! with
            {
                SubtypeField = "concurrent_type",
                Subtypes = [previousSubtype, concurrentSubtype],
            },
        };

        var restoredIntroduction = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            new MetadataV2Graph { Revision = 9, Resources = [concurrentlyExtendedResource] },
            new MetadataV2Graph { Revision = 7, Resources = [emptyResource] },
            new MetadataV2Graph { Revision = 8, Resources = [failedIntroducedResource] },
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var introducedSubtypes = restoredIntroduction.Resources.Should().ContainSingle().Which.Subtypes;
        introducedSubtypes!.SubtypeField.Should().Be("concurrent_type");
        var retainedSubtype = introducedSubtypes.Subtypes.Should().ContainSingle().Which;
        retainedSubtype.Code.GetInt32().Should().Be(2);
        retainedSubtype.Name.Should().Be("Concurrent subtype");
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_AlignsRepeatedSymbologyRuleAttributesByOccurrence()
    {
        var commercialRule = new Symbology3DRule
        {
            Attribute = "category",
            Comparison = Symbology3DComparison.Equals,
            Value = "commercial",
            Color = new Symbology3DColor(255, 0, 0),
        };
        var residentialRule = new Symbology3DRule
        {
            Attribute = "category",
            Comparison = Symbology3DComparison.Equals,
            Value = "residential",
            Color = new Symbology3DColor(0, 0, 255),
            Opacity = 0.5,
        };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-repeated-symbology" },
            Symbology3D = new Symbology3D
            {
                Rules = [commercialRule, residentialRule],
            },
        };
        var clearedResource = previousResource with { Symbology3D = null };
        var concurrentIndustrialRule = residentialRule with
        {
            Value = "industrial",
            Opacity = 0.8,
        };
        var concurrentlyRecreatedResource = clearedResource with
        {
            Symbology3D = previousResource.Symbology3D! with
            {
                Rules = [commercialRule, concurrentIndustrialRule],
            },
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            new MetadataV2Graph { Revision = 9, Resources = [concurrentlyRecreatedResource] },
            new MetadataV2Graph { Revision = 7, Resources = [previousResource] },
            new MetadataV2Graph { Revision = 8, Resources = [clearedResource] },
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var rules = compensation.Resources.Should().ContainSingle().Which.Symbology3D!.Rules;
        rules.Should().HaveCount(2);
        rules.Select(rule => rule.Value).Should().Equal("commercial", "industrial");
        rules[1].Opacity.Should().Be(0.8);
        rules.Should().NotContain(rule => rule.Value == "residential");
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresSchemaFieldsIndependently()
    {
        var previousField = new MetadataV2Field
        {
            SemanticId = "field.resource-layer-7.objectid",
            Name = "objectid",
            Type = MetadataV2FieldType.BigInteger,
            Title = "Original identifier",
        };
        var persistedField = previousField with
        {
            Type = MetadataV2FieldType.Integer,
            Title = "Published identifier",
        };
        var failedAddedField = new MetadataV2Field
        {
            Name = "publish_only",
            Type = MetadataV2FieldType.String,
            Title = "Publish-only field",
        };
        var concurrentField = new MetadataV2Field
        {
            Name = "concurrent_only",
            Type = MetadataV2FieldType.DateTime,
        };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-layer-7", Name = "Layer 7" },
            SchemaFields = [previousField],
        };
        var persistedResource = previousResource with
        {
            SchemaFields = [persistedField, failedAddedField],
        };
        var currentResource = persistedResource with
        {
            SchemaFields =
            [
                persistedField with { Title = "Concurrent identifier title" },
                failedAddedField with
                {
                    Name = "renamed_publish_only",
                    Title = "Concurrent publish-only title",
                },
                concurrentField,
            ],
        };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var fields = compensation.Resources.Should().ContainSingle().Which.SchemaFields;
        fields.Should().HaveCount(2);
        var restoredField = fields.Single(field => field.Name == previousField.Name);
        restoredField.Type.Should().Be(previousField.Type);
        restoredField.Title.Should().Be("Concurrent identifier title");
        fields.Should().NotContain(field => field.Name == failedAddedField.Name);
        fields.Should().NotContain(field => field.Name == "renamed_publish_only");
        fields.Should().ContainSingle(field => field.Name == concurrentField.Name).Which.Should().Be(concurrentField);
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_AlignsSchemaFieldAcrossRenames()
    {
        var previousField = new MetadataV2Field
        {
            Name = "original_name",
            Type = MetadataV2FieldType.BigInteger,
            Title = "Original name",
            Alias = "Original name",
            Description = "Original description",
        };
        var persistedField = previousField with
        {
            Name = "published_name",
            Title = "Published name",
            Alias = "Published name",
            Description = "Published description",
        };
        var currentField = persistedField with
        {
            Name = "concurrent_name",
            Title = "Concurrent name",
            Alias = "Concurrent name",
        };
        var previousResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-layer-rename" },
            SchemaFields = [previousField],
        };
        var persistedResource = previousResource with { SchemaFields = [persistedField] };
        var currentResource = persistedResource with { SchemaFields = [currentField] };
        var previous = new MetadataV2Graph { Revision = 7, Resources = [previousResource] };
        var persisted = previous with { Revision = 8, Resources = [persistedResource] };
        var current = persisted with { Revision = 9, Resources = [currentResource] };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var field = compensation.Resources.Should().ContainSingle().Which.SchemaFields
            .Should().ContainSingle().Which;
        field.Name.Should().Be("concurrent_name", "a later rename wins");
        field.Title.Should().Be("Concurrent name");
        field.Alias.Should().Be("Concurrent name");
        field.Description.Should().Be("Original description", "the aborted description change is reverted");
    }

    [Fact]
    public void BuildRebasedCompensatingMetadataV2Graph_RestoresBindingAndConnectionFieldsThreeWay()
    {
        var originalAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);
        var publishedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);
        var previousBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "binding-layer-7",
                Name = "imported-binding",
                UpdatedAt = originalAt,
            },
            ResourceId = "resource-original",
            ConnectionId = "connection-original",
            StorageType = MetadataV2StorageType.ObjectPrefix,
            Locator = "legacy/object",
            StorageLayerId = 99,
            Capabilities = [MetadataV2StorageBindingCapability.Download],
            Options = new Dictionary<string, JsonElement>
            {
                ["source"] = JsonSerializer.SerializeToElement("legacy"),
                ["schemaName"] = JsonSerializer.SerializeToElement("legacy_schema"),
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Draft,
                State = MetadataV2OperationalState.Unknown,
                ObservedAt = originalAt,
            },
        };
        var persistedBinding = previousBinding with
        {
            Metadata = previousBinding.Metadata with
            {
                Name = "binding-layer-7",
                UpdatedAt = publishedAt,
            },
            ResourceId = "resource-layer-7",
            ConnectionId = "connection-published",
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "honua.features",
            StorageLayerId = 7,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
            ],
            Options = new Dictionary<string, JsonElement>
            {
                ["schemaName"] = JsonSerializer.SerializeToElement("honua"),
                ["tableName"] = JsonSerializer.SerializeToElement("features"),
            },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["failed"] = JsonSerializer.SerializeToElement(true),
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = publishedAt,
            },
        };
        var currentBinding = persistedBinding with
        {
            Locator = "concurrent.table",
            Options = new Dictionary<string, JsonElement>
            {
                ["schemaName"] = JsonSerializer.SerializeToElement("honua"),
                ["tableName"] = JsonSerializer.SerializeToElement("features"),
                ["source"] = JsonSerializer.SerializeToElement("concurrent-source"),
                ["concurrent"] = JsonSerializer.SerializeToElement(true),
            },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["failed"] = JsonSerializer.SerializeToElement(true),
                ["concurrent"] = JsonSerializer.SerializeToElement(true),
            },
        };
        var previousConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "connection-published",
                Title = "Imported connection",
                UpdatedAt = originalAt,
            },
            Type = MetadataV2ConnectionType.HttpApi,
            Provider = "legacy",
            Endpoint = new Uri("https://legacy.example.test"),
            Options = new Dictionary<string, JsonElement>
            {
                ["mode"] = JsonSerializer.SerializeToElement("legacy"),
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Draft,
                State = MetadataV2OperationalState.Unknown,
                ObservedAt = originalAt,
            },
        };
        var persistedConnection = previousConnection with
        {
            Metadata = previousConnection.Metadata with
            {
                Title = "PostGIS secure connection",
                UpdatedAt = publishedAt,
            },
            Type = MetadataV2ConnectionType.Database,
            Provider = "postgis",
            Endpoint = null,
            Options = new Dictionary<string, JsonElement>(),
            Extensions = new Dictionary<string, JsonElement>
            {
                ["failed"] = JsonSerializer.SerializeToElement(true),
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = publishedAt,
            },
        };
        var currentConnection = persistedConnection with
        {
            SecretRef = "concurrent-secret",
            Options = new Dictionary<string, JsonElement>
            {
                ["concurrent"] = JsonSerializer.SerializeToElement(true),
            },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["failed"] = JsonSerializer.SerializeToElement(true),
                ["concurrent"] = JsonSerializer.SerializeToElement(true),
            },
        };
        var previous = new MetadataV2Graph
        {
            Revision = 7,
            StorageBindings = [previousBinding],
            Connections = [previousConnection],
        };
        var persisted = previous with
        {
            Revision = 8,
            StorageBindings = [persistedBinding],
            Connections = [persistedConnection],
        };
        var current = persisted with
        {
            Revision = 9,
            StorageBindings = [currentBinding],
            Connections = [currentConnection],
        };

        var compensation = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current,
            previous,
            persisted,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture));

        var binding = compensation.StorageBindings.Should().ContainSingle().Which;
        binding.Metadata.Name.Should().Be(previousBinding.Metadata.Name);
        binding.Metadata.UpdatedAt.Should().Be(originalAt);
        binding.ResourceId.Should().Be(previousBinding.ResourceId);
        binding.ConnectionId.Should().Be(previousBinding.ConnectionId);
        binding.StorageType.Should().Be(previousBinding.StorageType);
        binding.Locator.Should().Be("concurrent.table", "a later edit to the same field wins");
        binding.StorageLayerId.Should().Be(previousBinding.StorageLayerId);
        binding.Capabilities.Should().Equal(previousBinding.Capabilities);
        binding.Options.Should().ContainKey("schemaName").WhoseValue.GetString().Should().Be("legacy_schema");
        binding.Options.Should().ContainKey("source").WhoseValue.GetString().Should().Be("concurrent-source");
        binding.Options.Should().ContainKey("concurrent").WhoseValue.GetBoolean().Should().BeTrue();
        binding.Options.Should().NotContainKey("tableName");
        binding.Status.Should().Be(previousBinding.Status);
        binding.Extensions.Should().ContainKey("concurrent");
        binding.Extensions.Should().NotContainKey("failed");

        var connection = compensation.Connections.Should().ContainSingle().Which;
        connection.Metadata.Title.Should().Be(previousConnection.Metadata.Title);
        connection.Metadata.UpdatedAt.Should().Be(originalAt);
        connection.Type.Should().Be(previousConnection.Type);
        connection.Provider.Should().Be(previousConnection.Provider);
        connection.Endpoint.Should().Be(previousConnection.Endpoint);
        connection.SecretRef.Should().Be("concurrent-secret", "a later edit to the same field wins");
        connection.Options.Should().ContainKey("mode").WhoseValue.GetString().Should().Be("legacy");
        connection.Options.Should().ContainKey("concurrent").WhoseValue.GetBoolean().Should().BeTrue();
        connection.Status.Should().Be(previousConnection.Status);
        connection.Extensions.Should().ContainKey("concurrent");
        connection.Extensions.Should().NotContainKey("failed");
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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
    public void FindCanonicalGovernanceLink_PrefersManagedThenFallsBackToAuthoredRelation()
    {
        var metadata = new MetadataV2ObjectMetadata
        {
            Links =
            [
                new MetadataV2Link { Href = "https://example.test/imported-license", Rel = "license" },
                new MetadataV2Link
                {
                    Href = "https://example.test/managed-license",
                    Rel = "license",
                    ManagedBy = LayerSourceGovernance.LinkManager,
                },
                new MetadataV2Link { Href = "https://example.test/imported-source", Rel = "describedby" },
            ],
        };

        PostgreSqlLayerPublishingService.FindCanonicalGovernanceLink(metadata, "license")
            .Should().Be("https://example.test/managed-license");
        PostgreSqlLayerPublishingService.FindCanonicalGovernanceLink(metadata, "describedby")
            .Should().Be("https://example.test/imported-source");
    }

    [Fact]
    public void HydrateSourceGovernance_WithPersistedGraph_UsesSnapshotMetadata()
    {
        var metadata = new MetadataV2ObjectMetadata
        {
            Id = "resource-parcels",
            License = "MIT",
            Attribution = "County GIS",
            Links = [new MetadataV2Link { Href = "https://example.test/license", Rel = "license" }],
        };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-parcels", Name = "parcels" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                }
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = metadata,
                    PrimaryStorageBindingId = "binding-parcels",
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels" },
                    ResourceId = metadata.Id,
                    StorageLayerId = 7,
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "publication-parcels" },
                    ServiceId = "service-parcels",
                    ResourceId = metadata.Id,
                    StorageBindingId = "binding-parcels",
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
                    LayerIndex = 7,
                }
            ],
        };
        var layer = new PublishedLayerSummary
        {
            LayerId = 7,
            LayerName = "Parcels",
            Schema = "public",
            Table = "parcels",
            GeometryType = "Polygon",
            ServiceName = "parcels",
        };

        var hydrated = PostgreSqlLayerPublishingService.HydrateSourceGovernance(layer, graph, "parcels");

        hydrated.License.Should().Be("MIT");
        hydrated.Attribution.Should().Be("County GIS");
        hydrated.LicenseUrl.Should().Be("https://example.test/license");
        hydrated.LayerName.Should().Be(layer.LayerName);
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithActiveAndRetiredPublications_PrefersActive()
    {
        var retiredMetadata = new MetadataV2ObjectMetadata { Id = "resource-retired", License = "MIT" };
        var activeMetadata = new MetadataV2ObjectMetadata { Id = "resource-active", License = "CC-BY-4.0" };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-parcels", Name = "parcels" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = retiredMetadata,
                    PrimaryStorageBindingId = "binding-retired",
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired },
                },
                new MetadataV2Resource
                {
                    Metadata = activeMetadata,
                    PrimaryStorageBindingId = "binding-active",
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-retired" },
                    ResourceId = retiredMetadata.Id,
                    StorageLayerId = 7,
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-active" },
                    ResourceId = activeMetadata.Id,
                    StorageLayerId = 7,
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                },
            ],
            Publications =
            [
                CreatePublication(
                    "publication-retired",
                    "service-parcels",
                    retiredMetadata.Id,
                    "binding-retired",
                    7,
                    MetadataV2PublicationType.EsriFeatureLayer) with
                {
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired },
                },
                CreatePublication(
                    "publication-active",
                    "service-parcels",
                    activeMetadata.Id,
                    "binding-active",
                    7,
                    MetadataV2PublicationType.EsriFeatureLayer) with
                {
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                },
            ],
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "parcels");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, activeMetadata));

        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var retiredStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var bindingLifecycleGraph = graph with
        {
            Resources = graph.Resources.Select(resource => resource with { Status = activeStatus }).ToArray(),
            StorageBindings =
            [
                graph.StorageBindings[0] with { Status = retiredStatus },
                graph.StorageBindings[1] with { Status = activeStatus },
            ],
            Publications = graph.Publications.Select(publication => publication with { Status = activeStatus }).ToArray(),
        };
        var bindingLifecycleResult = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(
            bindingLifecycleGraph,
            "parcels");
        bindingLifecycleResult.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, activeMetadata));

        var retiredOnlyGraph = graph with
        {
            Resources = [graph.Resources[0]],
            StorageBindings = [graph.StorageBindings[0]],
            Publications = [graph.Publications[0]],
        };
        var retiredOnlyResult = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(
            retiredOnlyGraph,
            "parcels");
        retiredOnlyResult.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, retiredMetadata));

        var deprecatedStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated };
        var deprecatedGraph = graph with
        {
            Resources = [graph.Resources[0], graph.Resources[1] with { Status = deprecatedStatus }],
            Publications = [graph.Publications[0], graph.Publications[1] with { Status = deprecatedStatus }],
        };
        var deprecatedResult = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(
            deprecatedGraph,
            "parcels");
        deprecatedResult.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(7, activeMetadata));
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithActiveAndNonServingSameNameServices_PrefersActive()
    {
        var retiredMetadata = new MetadataV2ObjectMetadata { Id = "resource-retired", License = "MIT" };
        var activeMetadata = new MetadataV2ObjectMetadata { Id = "resource-active", License = "CC-BY-4.0" };
        var retiredStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-retired", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    Status = retiredStatus,
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-active", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    Status = activeStatus,
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = retiredMetadata,
                    PrimaryStorageBindingId = "binding-retired",
                    Status = retiredStatus,
                },
                new MetadataV2Resource
                {
                    Metadata = activeMetadata,
                    PrimaryStorageBindingId = "binding-active",
                    Status = activeStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-retired" },
                    ResourceId = retiredMetadata.Id,
                    StorageLayerId = 7,
                    Status = retiredStatus,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-active" },
                    ResourceId = activeMetadata.Id,
                    StorageLayerId = 8,
                    Status = activeStatus,
                },
            ],
            Publications =
            [
                CreatePublication(
                    "publication-retired",
                    "service-retired",
                    retiredMetadata.Id,
                    "binding-retired",
                    7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = retiredStatus },
                CreatePublication(
                    "publication-active",
                    "service-active",
                    activeMetadata.Id,
                    "binding-active",
                    8,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
            ],
        };

        var result = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        result.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(8, activeMetadata));

        var draftStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft };
        var draftGraph = graph with
        {
            Resources = [graph.Resources[0] with { Status = draftStatus }, graph.Resources[1]],
            Publications = [graph.Publications[0] with { Status = draftStatus }, graph.Publications[1]],
        };

        var draftResult = PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(
            draftGraph,
            "shared");

        draftResult.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<int, MetadataV2ObjectMetadata>(8, activeMetadata));
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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
    public void IndexSourceGovernanceByStorageLayer_WithDisabledExactIdCollision_ReturnsConflict()
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

        var action = () => PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique Esri FeatureServer service");
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithDisabledNameOnly_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-disabled", Name = "shared" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-disabled" } },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-disabled" },
                    ResourceId = "resource-disabled",
                    StorageLayerId = 7,
                },
            ],
            Publications =
            [
                CreatePublication(
                    "pub-disabled",
                    "service-disabled",
                    "resource-disabled",
                    "binding-disabled",
                    0,
                    MetadataV2PublicationType.EsriFeatureLayer),
            ],
        };

        var action = () => PostgreSqlLayerPublishingService.IndexSourceGovernanceByStorageLayer(graph, "shared");

        var exception = action.Should().Throw<LayerPublishingException>().Which;
        exception.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        exception.Message.Should().Contain("does not resolve to one unique Esri FeatureServer service");
    }

    [Fact]
    public void IndexSourceGovernanceByStorageLayer_WithProtocolAggregateAndDedicatedFeatureServices_UsesDedicatedFeatureResource()
    {
        var featureMetadata = new MetadataV2ObjectMetadata { Id = "resource-feature", License = "CC-BY-4.0" };
        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-aggregate", Name = "shared" },
                    Protocols = [ServiceProtocols.FeatureServer, ServiceProtocols.OgcFeatures],
                    Status = activeStatus,
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature", Name = "shared" },
                    Protocols = [ServiceProtocols.FeatureServer],
                    Status = activeStatus,
                }
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-aggregate", License = "MIT" },
                    Status = activeStatus,
                },
                new MetadataV2Resource { Metadata = featureMetadata, Status = activeStatus }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-aggregate" },
                    ResourceId = "resource-aggregate",
                    StorageLayerId = 7,
                    Status = activeStatus,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-feature" },
                    ResourceId = "resource-feature",
                    StorageLayerId = 7,
                    Status = activeStatus,
                }
            ],
            Publications =
            [
                CreatePublication(
                    "pub-aggregate",
                    "service-aggregate",
                    "resource-aggregate",
                    "binding-aggregate",
                    7,
                    MetadataV2PublicationType.OgcCollection) with { Status = activeStatus },
                CreatePublication(
                    "pub-feature",
                    "service-feature",
                    "resource-feature",
                    "binding-feature",
                    7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus }
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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
    public void BuildLayerEnabledMetadataV2Graph_UpdatesAffectedResourceAndPublicationLifecycles()
    {
        var originalCondition = new MetadataV2Condition { Type = "Original", Status = "True" };
        var activeStatus = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
            Conditions = [originalCondition],
            ObservedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
        };
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-a" },
                    PublicationIds = ["pub-7-a", "pub-8"],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-b" },
                    PublicationIds = ["pub-7-b"],
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-7" },
                    StorageBindingIds = ["binding-7"],
                    PrimaryStorageBindingId = "binding-7",
                    Status = activeStatus,
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-8" },
                    StorageBindingIds = ["binding-8"],
                    PrimaryStorageBindingId = "binding-8",
                    Status = activeStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-7" },
                    ResourceId = "resource-7",
                    StorageLayerId = 7,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-8" },
                    ResourceId = "resource-8",
                    StorageLayerId = 8,
                },
            ],
            Publications =
            [
                CreatePublication("pub-7-a", "service-a", "resource-7", "binding-7", 7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
                CreatePublication("pub-7-b", "service-b", "resource-7", "binding-7", 7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
                CreatePublication("pub-8", "service-a", "resource-8", "binding-8", 8,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
            ],
        };
        var disabledAt = DateTimeOffset.Parse("2026-08-11T18:00:00Z", CultureInfo.InvariantCulture);

        var disabled = PostgreSqlLayerPublishingService.BuildLayerEnabledMetadataV2Graph(
            graph,
            [7],
            enabled: false,
            disabledAt);

        disabled.Revision.Should().Be(8);
        disabled.Resources.Single(resource => resource.Metadata.Id == "resource-7").Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Retired);
        disabled.Resources.Single(resource => resource.Metadata.Id == "resource-7").Status.Conditions
            .Should().Equal(originalCondition);
        disabled.Publications.Where(publication => publication.ResourceId == "resource-7")
            .Should().AllSatisfy(publication =>
                publication.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired));
        disabled.Resources.Single(resource => resource.Metadata.Id == "resource-8").Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);
        disabled.Publications.Single(publication => publication.Metadata.Id == "pub-8").Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);

        var enabledAt = DateTimeOffset.Parse("2026-08-11T18:05:00Z", CultureInfo.InvariantCulture);
        var enabled = PostgreSqlLayerPublishingService.BuildLayerEnabledMetadataV2Graph(
            disabled,
            [7, 8],
            enabled: true,
            enabledAt);

        enabled.Revision.Should().Be(9);
        enabled.Resources.Should().AllSatisfy(resource =>
            resource.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active));
        enabled.Publications.Should().AllSatisfy(publication =>
            publication.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active));
        enabled.Resources.Should().AllSatisfy(resource => resource.Status.ObservedAt.Should().Be(enabledAt));
        enabled.Publications.Should().AllSatisfy(publication => publication.Status.ObservedAt.Should().Be(enabledAt));
    }

    [Fact]
    public void BuildLayerEnabledMetadataV2Graph_WithSharedResource_OnlyRetiresAffectedBinding()
    {
        var activeStatus = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
        };
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-shared" },
                    StorageBindingIds = ["binding-7", "binding-8"],
                    PrimaryStorageBindingId = "binding-7",
                    Status = activeStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-7" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 7,
                    Status = activeStatus,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-8" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 8,
                    Status = activeStatus,
                },
            ],
            Publications =
            [
                CreatePublication("pub-7", "service-a", "resource-shared", "binding-7", 7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
                CreatePublication("pub-8", "service-a", "resource-shared", "binding-8", 8,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
            ],
        };
        var disabledAt = DateTimeOffset.Parse("2026-08-11T19:10:00Z", CultureInfo.InvariantCulture);

        var firstDisabled = PostgreSqlLayerPublishingService.BuildLayerEnabledMetadataV2Graph(
            graph,
            [7],
            enabled: false,
            disabledAt);

        firstDisabled.StorageBindings.Single(binding => binding.Metadata.Id == "binding-7")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
        firstDisabled.StorageBindings.Single(binding => binding.Metadata.Id == "binding-8")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        firstDisabled.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);
        firstDisabled.Publications.Single(publication => publication.Metadata.Id == "pub-7")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
        firstDisabled.Publications.Single(publication => publication.Metadata.Id == "pub-8")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);

        var deprecatedStatus = activeStatus with { Lifecycle = MetadataV2LifecycleStatus.Deprecated };
        var deprecatedSiblingGraph = graph with
        {
            StorageBindings =
            [
                graph.StorageBindings[0],
                graph.StorageBindings[1] with { Status = deprecatedStatus },
            ],
            Publications =
            [
                graph.Publications[0],
                graph.Publications[1] with { Status = deprecatedStatus },
            ],
        };
        var targetDisabledWithDeprecatedSibling =
            PostgreSqlLayerPublishingService.BuildLayerEnabledMetadataV2Graph(
                deprecatedSiblingGraph,
                [7],
                enabled: false,
                disabledAt);
        targetDisabledWithDeprecatedSibling.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Deprecated);
        targetDisabledWithDeprecatedSibling.Publications.Single(publication => publication.Metadata.Id == "pub-8")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Deprecated);

        var allDisabled = PostgreSqlLayerPublishingService.BuildLayerEnabledMetadataV2Graph(
            firstDisabled,
            [8],
            enabled: false,
            disabledAt.AddMinutes(1));

        allDisabled.StorageBindings.Should().AllSatisfy(binding =>
            binding.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired));
        allDisabled.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Retired);
        allDisabled.Publications.Should().AllSatisfy(publication =>
            publication.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired));
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
                    Protocols = [ServiceProtocols.FeatureServer],
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
    public void BuildLinkedLayerMetadataV2Graph_WithOtherProtocolAtTargetIndex_AddsFeaturePublication()
    {
        var graph = new MetadataV2Graph
        {
            Revision = 4,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    PublicationIds = ["pub-ogc-101"],
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" },
                    PrimaryStorageBindingId = "binding-alpha",
                    StorageBindingIds = ["binding-alpha"],
                },
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-beta" },
                    PrimaryStorageBindingId = "binding-beta",
                    StorageBindingIds = ["binding-beta"],
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-alpha" },
                    ResourceId = "resource-alpha",
                    StorageLayerId = 101,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-beta" },
                    ResourceId = "resource-beta",
                    StorageLayerId = 202,
                },
            ],
            Publications =
            [
                CreatePublication(
                    "pub-ogc-101",
                    "service-beta",
                    "resource-beta",
                    "binding-beta",
                    101,
                    MetadataV2PublicationType.OgcCollection),
            ],
        };

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Governed parcels",
            4326,
            DateTimeOffset.Parse("2026-08-11T08:00:00Z", CultureInfo.InvariantCulture),
            enabled: false);

        updated.Publications.Should().ContainSingle(publication =>
            publication.Metadata.Id == "pub-ogc-101" &&
            publication.PublicationType == MetadataV2PublicationType.OgcCollection);
        updated.Publications.Should().ContainSingle(publication =>
            publication.ServiceId == "service-beta" &&
            publication.ResourceId == "resource-alpha" &&
            publication.StorageBindingId == "binding-alpha" &&
            publication.LayerIndex == 101 &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);
        updated.Resources.Single(resource => resource.Metadata.Id == "resource-alpha").Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Retired);
        updated.Publications.Single(publication =>
                publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer)
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_ReactivatesPublicationsAcrossServices()
    {
        var retiredStatus = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Retired,
            State = MetadataV2OperationalState.Ready,
        };
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    PublicationIds = ["pub-alpha"],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-beta", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    PublicationIds = ["pub-beta"],
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-shared" },
                    StorageBindingIds = ["binding-shared"],
                    PrimaryStorageBindingId = "binding-shared",
                    Status = retiredStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-shared" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 101,
                },
            ],
            Publications =
            [
                CreatePublication("pub-alpha", "service-alpha", "resource-shared", "binding-shared", 101,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = retiredStatus },
                CreatePublication("pub-beta", "service-beta", "resource-shared", "binding-shared", 101,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = retiredStatus },
            ],
        };
        var enabledAt = DateTimeOffset.Parse("2026-08-11T18:10:00Z", CultureInfo.InvariantCulture);

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "beta",
            101,
            "Shared layer",
            4326,
            enabledAt,
            enabled: true);

        updated.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);
        updated.Publications.Should().HaveCount(2);
        updated.Publications.Should().AllSatisfy(publication =>
        {
            publication.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
            publication.Status.ObservedAt.Should().Be(enabledAt);
        });
    }

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithSharedResource_OnlyRetiresTargetBinding()
    {
        var activeStatus = new MetadataV2Status
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
        };
        var graph = new MetadataV2Graph
        {
            Revision = 7,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-alpha", Name = "alpha" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                    PublicationIds = ["pub-7", "pub-8"],
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource-shared" },
                    StorageBindingIds = ["binding-7", "binding-8"],
                    PrimaryStorageBindingId = "binding-7",
                    Status = activeStatus,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-7" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 7,
                    Status = activeStatus,
                },
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-8" },
                    ResourceId = "resource-shared",
                    StorageLayerId = 8,
                    Status = activeStatus,
                },
            ],
            Publications =
            [
                CreatePublication("pub-7", "service-alpha", "resource-shared", "binding-7", 7,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
                CreatePublication("pub-8", "service-alpha", "resource-shared", "binding-8", 8,
                    MetadataV2PublicationType.EsriFeatureLayer) with { Status = activeStatus },
            ],
        };

        var updated = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph,
            "alpha",
            7,
            "Primary layer",
            4326,
            DateTimeOffset.Parse("2026-08-11T19:30:00Z", CultureInfo.InvariantCulture),
            enabled: false);

        updated.StorageBindings.Single(binding => binding.Metadata.Id == "binding-7")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
        updated.StorageBindings.Single(binding => binding.Metadata.Id == "binding-8")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        updated.Resources.Should().ContainSingle().Which.Status.Lifecycle
            .Should().Be(MetadataV2LifecycleStatus.Active);
        updated.Publications.Single(publication => publication.Metadata.Id == "pub-7")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Retired);
        updated.Publications.Single(publication => publication.Metadata.Id == "pub-8")
            .Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
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
        var updatedPublication = updated.Publications.Should().ContainSingle().Which;
        updatedPublication.Should().BeEquivalentTo(
            existingPublication,
            options => options.Excluding(publication => publication.Status));
        updatedPublication.Status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        updatedPublication.Status.State.Should().Be(MetadataV2OperationalState.Ready);
        updatedPublication.Status.ObservedAt.Should().Be(now);
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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
            Route = "/custom/beta/FeatureServer",
            Protocols = [ServiceProtocols.FeatureServer],
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
    public void BuildLinkedLayerMetadataV2Graph_WithNonFeatureExactIdCollision_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "beta", Name = "disabled-name" },
                    ServiceType = MetadataV2ServiceType.OgcApiFeatures,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-enabled", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                },
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" } },
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

    [Fact]
    public void BuildLinkedLayerMetadataV2Graph_WithDisabledNameOnly_ReturnsConflict()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-disabled", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.OgcFeatures],
                },
            ],
            Resources =
            [
                new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource-alpha" } },
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
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-feature-b", Name = "beta" },
                    ServiceType = MetadataV2ServiceType.EsriFeatureService,
                    Protocols = [ServiceProtocols.FeatureServer],
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

    private sealed class IndeterminateCommitGraphStore(MetadataV2Graph pendingGraph) : IMetadataV2GraphStore
    {
        private MetadataV2GraphSnapshot _current = new(pendingGraph, "\"pending\"", DateTimeOffset.UtcNow);

        public List<MetadataV2Graph> SavedGraphs { get; } = [];

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_current);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(
                _current.Revision == revision ? _current : null);

        public Task<MetadataV2GraphSnapshot> SaveAsync(
            MetadataV2Graph graph,
            string? expectedEtag,
            CancellationToken cancellationToken = default)
        {
            SavedGraphs.Add(graph);
            if (SavedGraphs.Count == 1)
            {
                throw new MetadataV2GraphCommitOutcomeUnknownException(
                    _current,
                    "123",
                    new IOException("commit acknowledgement lost"));
            }

            _current = new MetadataV2GraphSnapshot(graph, "\"compensated\"", DateTimeOffset.UtcNow);
            return Task.FromResult(_current);
        }

        public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
            long revision,
            string? expectedCurrentEtag,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_current.Revision != revision)
            {
                throw new InvalidOperationException($"Metadata v2 revision {revision} is not retained by this test store.");
            }

            if (expectedCurrentEtag is not null &&
                !string.Equals(expectedCurrentEtag, _current.Etag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The current Metadata v2 ETag changed before activation.");
            }

            return Task.FromResult(_current);
        }
    }

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
