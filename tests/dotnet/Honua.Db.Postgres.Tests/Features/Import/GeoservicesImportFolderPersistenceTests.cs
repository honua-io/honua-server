// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Migration.Services;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportSubtypePersistenceTests
{
    [IntegrationTest]
    public async Task ImportLayerAsync_ReplacementTargetsAnotherService_DoesNotRebindTheExistingPublication()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportReplacementTarget");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var store = new PostgresMetadataV2GraphStore(new FixtureConnectionProvider(fixture), $"Replacement-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        var originalName = $"original_{Guid.NewGuid():N}";
        var replacementName = $"replacement_{Guid.NewGuid():N}";
        const string url = "https://example.com/arcgis/rest/services/Survey/Parcels/FeatureServer";
        try
        {
            var initial = await store.GetCurrentAsync();
            var target = GeoservicesImportTargetPlanner.Plan(initial.Graph, new()
            {
                ServiceUrl = url, ServiceName = originalName, PublicName = "Parcels"
            }, null);
            var importer = CreateService(store, schema, false,
                handler: new SubtypeFeatureServerHandler(false, false, false, false, false,
                    "/arcgis/rest/services/Survey/Parcels/FeatureServer"));
            var request = new GeoservicesImportRequest
            {
                ServiceUrl = url, LayerId = 0, TableName = "existing_parcels", TargetSchema = schema,
                BatchSize = 10, MaxRetries = 0, ServiceName = target.ServiceName, Target = target
            };
            (await importer.ImportLayerAsync(request)).PublishedLayerId.Should().NotBeNull();
            var before = await store.GetCurrentAsync();
            var replacement = GeoservicesImportTargetPlanner.Plan(before.Graph, new()
            {
                ServiceUrl = url, ServiceName = replacementName, PublicName = "Different", FolderId = new(target.FolderId)
            }, null);
            var result = await importer.ImportLayerAsync(request with
            {
                OverwriteExisting = true, ServiceName = replacement.ServiceName, Target = replacement
            });
            result.DataCommitted.Should().BeTrue(result.ErrorMessage);
            result.PublishedLayerId.Should().BeNull();
            var after = await store.GetCurrentAsync();
            after.Etag.Should().Be(before.Etag);
            after.Graph.Services.Should().NotContain(service => service.Metadata.Name == replacementName);
            after.Graph.Services.Single(service => service.Metadata.Name == originalName).FolderId.Should().Be(target.FolderId);
        }
        finally
        {
            await CleanupCatalogAsync(originalName);
            await CleanupCatalogAsync(replacementName);
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportLayerAsync_DuplicateSourceLeaves_PreservesBothPublicPathsAndAllocatesDistinctLogicalNames()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportCollision");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var store = new PostgresMetadataV2GraphStore(new FixtureConnectionProvider(fixture), $"Collision-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        var targets = new List<GeoservicesImportTarget>();
        try
        {
            foreach (var folder in new[] { "Internal", "Public" })
            {
                var path = $"/arcgis/rest/services/{folder}/Parcels/FeatureServer";
                var selection = new GeoservicesImportSelection { ServiceUrl = $"https://example.com{path}" };
                var snapshot = await store.GetCurrentAsync();
                var target = GeoservicesImportTargetPlanner.Plan(snapshot.Graph, selection, null);
                targets.Add(target);
                var importer = CreateService(store, schema, false,
                    handler: new SubtypeFeatureServerHandler(false, false, false, false, false, path));
                var result = await importer.ImportLayerAsync(new GeoservicesImportRequest
                {
                    ServiceUrl = selection.ServiceUrl,
                    LayerId = 0,
                    TableName = $"parcels_{folder.ToLowerInvariant()}",
                    TargetSchema = schema,
                    BatchSize = 10,
                    MaxRetries = 0,
                    ServiceName = target.ServiceName,
                    Target = target
                });
                result.PublishedLayerId.Should().NotBeNull(string.Join("; ", result.Warnings));
            }
            targets[0].ServiceName.Should().NotBe(targets[1].ServiceName);
            var persisted = await store.GetCurrentAsync();
            foreach (var target in targets)
            {
                var service = persisted.Graph.Services.Single(service => service.Metadata.Name == target.ServiceName);
                service.Metadata.Title.Should().Be("Parcels");
                MetadataV2ServicePaths.QualifiedName(persisted.Graph, service).Should().Be($"{target.FolderName}/Parcels");
                persisted.Graph.ServicePathAliases.Should().NotContain(alias => alias.FolderName == null &&
                    alias.PublicName == target.ServiceName && alias.TargetFacetIds.Contains(service.Metadata.Id),
                    "a generated logical name never creates a public root alias");
            }
        }
        finally
        {
            foreach (var target in targets) await CleanupCatalogAsync(target.ServiceName);
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData("source", false)]
    [InlineData("root", false)]
    [InlineData("remap", false)]
    [InlineData("source", true)]
    [InlineData("root", true)]
    [InlineData("remap", true)]
    public async Task ImportLayerAsync_PlannedSourceFolder_PersistsFolderAndPublicIdentityAcrossRestart(string mapping, bool namespaced)
    {
        var serviceName = $"import_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportFolder");
        var environment = $"ImportFolder-{Guid.NewGuid():N}";
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var graphStore = new PostgresMetadataV2GraphStore(new FixtureConnectionProvider(fixture), environment,
            FixtureBypassDatabaseSchemaGuard.Instance);
        var initial = await graphStore.GetCurrentAsync();
        var target = new GeoservicesImportTarget
        {
            ServiceName = serviceName,
            ServiceId = $"svc-import-{Guid.NewGuid():N}",
            PublicName = "Parcels",
            FolderId = mapping == "root" ? null : $"folder-{Guid.NewGuid():N}",
            FolderName = mapping == "root" ? null : mapping == "remap" ? "Target" : "Survey",
            Namespace = namespaced ? "published" : null,
            TenantId = namespaced ? "owner" : null
        };
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(context => context.TenantId).Returns("owner");

        try
        {
            if (mapping == "remap")
                await graphStore.SaveAsync(MetadataV2FolderMutations.Create(initial.Graph, target.CreateFolderDefinition()!), initial.Etag);
            var importer = CreateService(graphStore, schemaName, false,
                handler: new SubtypeFeatureServerHandler(false, false, false, false, false,
                    "/arcgis/rest/services/Survey/Parcels/FeatureServer"), tenantContext: tenant.Object);
            var request = new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Survey/Parcels/FeatureServer",
                LayerId = 0,
                TableName = "folder_parcels",
                TargetSchema = schemaName,
                BatchSize = 10,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName,
                Target = target
            };
            var result = await importer.ImportLayerAsync(request);

            result.DataCommitted.Should().BeTrue(result.ErrorMessage);
            result.PublishedLayerId.Should().NotBeNull(string.Join("; ", result.Warnings));
            var restarted = new PostgresMetadataV2GraphStore(new FixtureConnectionProvider(fixture), environment,
                FixtureBypassDatabaseSchemaGuard.Instance);
            var snapshot = await restarted.GetCurrentAsync();
            var published = snapshot.Graph.Services.Single(service => service.Metadata.Name == serviceName);
            published.FolderId.Should().Be(target.FolderId);
            published.EffectivePublicName.Should().Be("Parcels");
            published.Metadata.Title.Should().Be("Parcels");
            published.Metadata.Tenant.Should().Be(target.TenantId);
            published.Metadata.Namespace.Should().Be(target.Namespace);
            MetadataV2ServicePaths.QualifiedName(snapshot.Graph, published)
                .Should().Be(target.FolderName is null ? "Parcels" : $"{target.FolderName}/Parcels");
            snapshot.Graph.Services.Select(service => service.Metadata.Id)
                .Should().Contain(initial.Graph.Services.Select(service => service.Metadata.Id));
            snapshot.Graph.Folders.Count(folder => !folder.Deleted && folder.Metadata.Id == target.FolderId)
                .Should().Be(mapping == "root" ? 0 : 1);

            var retried = await importer.ImportLayerAsync(request with { OverwriteExisting = true });
            retried.PublishedLayerId.Should().Be(result.PublishedLayerId, string.Join("; ", retried.Warnings));
            var afterRetry = await restarted.GetCurrentAsync();
            afterRetry.Graph.Services.Should().ContainSingle(service => service.Metadata.Name == serviceName)
                .Which.Metadata.Id.Should().Be(target.ServiceId);
            afterRetry.Graph.Folders.Count(folder => !folder.Deleted && folder.Metadata.Id == target.FolderId)
                .Should().Be(mapping == "root" ? 0 : 1);

            var existingTarget = GeoservicesImportTargetPlanner.Plan(afterRetry.Graph, new()
            {
                ServiceUrl = request.ServiceUrl, ServiceName = serviceName, Namespace = target.Namespace
            }, "owner");
            existingTarget.ExistingService.Should().BeTrue();
            var additional = await importer.ImportLayerAsync(request with
            {
                TableName = "additional_parcels", Target = existingTarget
            });
            additional.PublishedLayerId.Should().NotBeNull(string.Join("; ", additional.Warnings));
            additional.PublishedLayerId.Should().NotBe(result.PublishedLayerId);
            var afterAdditional = await restarted.GetCurrentAsync();
            var unchangedService = afterAdditional.Graph.Services.Single(service => service.Metadata.Name == serviceName);
            unchangedService.Metadata.Id.Should().Be(target.ServiceId);
            unchangedService.FolderId.Should().Be(target.FolderId);
            unchangedService.EffectivePublicName.Should().Be(target.PublicName);
            afterAdditional.Graph.Publications.Where(publication => publication.ServiceId == target.ServiceId)
                .Select(publication => publication.ResourceId).Distinct().Should().HaveCount(2);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }
}
