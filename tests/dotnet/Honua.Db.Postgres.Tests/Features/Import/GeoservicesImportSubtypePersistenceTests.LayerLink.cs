// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportSubtypePersistenceTests
{
    public enum TargetServicePresence { New, Existing }

    [Theory]
    [InlineData(TargetServicePresence.New, MetadataV2LifecycleStatus.Retired)]
    [InlineData(TargetServicePresence.New, MetadataV2LifecycleStatus.Active)]
    [InlineData(TargetServicePresence.Existing, MetadataV2LifecycleStatus.Retired)]
    [InlineData(TargetServicePresence.Existing, MetadataV2LifecycleStatus.Active)]
    public async Task LinkExistingLayerToServiceAsync_StatusExtensions_SurviveColdReload(TargetServicePresence targetPresence, MetadataV2LifecycleStatus lifecycle)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("LinkStatus");
        var sourceService = $"link_source_{Guid.NewGuid():N}";
        var targetService = $"{sourceService}_target";
        var environment = $"LinkStatus-{Guid.NewGuid():N}";
        try
        {
            await EnsureCatalogSchemaAsync();
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
            await fixture.ExecuteDdlUnderLockAsync(
                $"CREATE TABLE \"{schema}\".features (LIKE honua.features INCLUDING ALL);");
            var store = CreateLinkGraphStore(environment);
            var publisher = CreateLayerPublisher(store,
                new PostgresSchemaConfiguration("honua", schema, [schema, "public"]), null);
            var layer = await PublishLinkSourcesAsync(publisher, schema, sourceService);
            if (targetPresence == TargetServicePresence.Existing)
            {
                await publisher.LinkExistingLayerToServiceAsync(fixture.ConnectionString, layer.LayerId, targetService, true);
            }
            var current = await store.GetCurrentAsync();
            await store.SaveAsync(WithRetainedStatuses(current.Graph), current.Etag);
            var before = await CreateLinkGraphStore(environment).GetCurrentAsync();

            var result = await publisher.LinkExistingLayerToServiceAsync(
                fixture.ConnectionString, layer.LayerId, targetService, lifecycle == MetadataV2LifecycleStatus.Active);

            result.Should().NotBeNull();
            var after = await CreateLinkGraphStore(environment).GetCurrentAsync();
            after.Revision.Should().BeGreaterThan(before.Revision);
            AssertLinkedStatusRetention(before.Graph, after.Graph, layer.LayerId);
            after.Graph.StorageBindings.Single(binding => binding.StorageLayerId == layer.LayerId)
                .Status.Lifecycle.Should().Be(lifecycle);
        }
        finally
        {
            await CleanupCatalogAsync(targetService);
            await CleanupCatalogAsync(sourceService);
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresMetadataV2GraphStore CreateLinkGraphStore(string environment)
        => new(new FixtureConnectionProvider(fixture), environment, FixtureBypassDatabaseSchemaGuard.Instance);

    private async Task<PublishedLayerSummary> PublishLinkSourcesAsync(
        PostgreSqlLayerPublishingService publisher, string schema, string serviceName)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = $"{schema},public"
        }.ConnectionString;
        PublishedLayerSummary? source = null;
        foreach (var table in new[] { "link_source", "link_unrelated" })
        {
            await fixture.ExecuteDdlUnderLockAsync($"""
                CREATE TABLE "{schema}"."{table}" (objectid integer PRIMARY KEY, geometry geometry(Point, 4326));
                INSERT INTO "{schema}"."{table}" VALUES (1, ST_SetSRID(ST_MakePoint(1, 2), 4326));
                """);
            var layer = await publisher.PublishLayerAsync(connectionString, new LayerPublishRequest
            {
                Schema = schema,
                Table = table,
                LayerName = table,
                PrimaryKey = "objectid",
                GeometryColumn = "geometry",
                ServiceName = serviceName
            });
            source ??= layer;
        }
        return source!;
    }

    private static MetadataV2Graph WithRetainedStatuses(MetadataV2Graph graph)
        => graph with
        {
            Resources = graph.Resources.Select(dataset => dataset with
            {
                Status = RetainedStatus(dataset.Metadata.Id)
            }).ToArray(),
            StorageBindings = graph.StorageBindings.Select(binding => binding with
            {
                Status = RetainedStatus(binding.Metadata.Id)
            }).ToArray(),
            Publications = graph.Publications.Select(layer => layer with
            {
                Status = RetainedStatus(layer.Metadata.Id)
            }).ToArray()
        };

    private static MetadataV2Status RetainedStatus(string identity)
        => new()
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
            ObservedAt = DateTimeOffset.UnixEpoch,
            AdditionalProperties = new() { ["futureStatus"] = JsonSerializer.SerializeToElement(identity) },
            Conditions =
            [
                new MetadataV2Condition
                {
                    Type = "future-readiness",
                    Status = "True",
                    AdditionalProperties = new() { ["futureCondition"] = JsonSerializer.SerializeToElement(identity) }
                }
            ]
        };

    private static void AssertLinkedStatusRetention(MetadataV2Graph before, MetadataV2Graph after, int storageLayerId)
    {
        var binding = after.StorageBindings.Single(candidate => candidate.StorageLayerId == storageLayerId);
        var expectedBinding = before.StorageBindings.Single(candidate => candidate.Metadata.Id == binding.Metadata.Id);
        AssertRetainedStatus(expectedBinding.Status, binding.Status);
        binding.Status.ObservedAt.Should().BeAfter(DateTimeOffset.UnixEpoch);
        binding.Status.State.Should().Be(MetadataV2OperationalState.Ready);
        foreach (var previous in before.Publications)
        {
            var layer = after.Publications.Single(candidate => candidate.Metadata.Id == previous.Metadata.Id);
            AssertRetainedStatus(previous.Status, layer.Status);
            if (layer.StorageBindingId == binding.Metadata.Id)
            {
                layer.Status.Lifecycle.Should().Be(binding.Status.Lifecycle);
                layer.Status.ObservedAt.Should().Be(binding.Status.ObservedAt);
            }
            else
            {
                layer.Should().BeEquivalentTo(previous, options => options.ComparingRecordsByMembers());
            }
        }
        foreach (var layer in after.Publications.Where(candidate => before.Publications.All(
                     previous => previous.Metadata.Id != candidate.Metadata.Id)))
        {
            layer.Status.AdditionalProperties.Should().BeNullOrEmpty();
            layer.Status.Conditions.Should().BeEmpty();
        }
        foreach (var previous in before.Resources)
        {
            AssertRetainedStatus(previous.Status, after.Resources.Single(
                dataset => dataset.Metadata.Id == previous.Metadata.Id).Status);
        }
        after.StorageBindings.Where(candidate => candidate.Metadata.Id != binding.Metadata.Id)
            .Should().BeEquivalentTo(before.StorageBindings.Where(candidate => candidate.Metadata.Id != binding.Metadata.Id));
    }

    private static void AssertRetainedStatus(MetadataV2Status expected, MetadataV2Status actual)
    {
        actual.AdditionalProperties.Should().NotBeNull();
        actual.AdditionalProperties!.Keys.Should().BeEquivalentTo(expected.AdditionalProperties!.Keys);
        foreach (var property in expected.AdditionalProperties)
        {
            JsonElement.DeepEquals(actual.AdditionalProperties[property.Key], property.Value)
                .Should().BeTrue($"status property {property.Key} must retain its own entity value");
        }
        actual.Conditions.Should().Equal(expected.Conditions);
    }
}
