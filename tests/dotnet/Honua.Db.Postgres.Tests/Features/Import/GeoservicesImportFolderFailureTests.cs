// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Moq;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportSubtypePersistenceTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportLayerAsync_PublicationCommitFails_CompensatesFolderAndServiceWithoutLosingConcurrentChanges(bool concurrentChange)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportCommitFailure");
        var trigger = $"reject_import_{Guid.NewGuid():N}";
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var realStore = new PostgresMetadataV2GraphStore(new FixtureConnectionProvider(fixture), $"CommitFailure-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        var initial = await realStore.GetCurrentAsync();
        const string sourceUrl = "https://example.com/arcgis/rest/services/Survey/Parcels/FeatureServer";
        var target = GeoservicesImportTargetPlanner.Plan(initial.Graph, new()
        {
            ServiceUrl = sourceUrl, ServiceName = $"failed_{Guid.NewGuid():N}", PublicName = "Parcels"
        }, null);
        var concurrentFolder = new MetadataV2Folder { Metadata = new() { Id = "concurrent-folder", Name = "Concurrent" } };
        var firstGraphSaved = false;
        var concurrentGraphSaved = false;
        var publicationLogger = new Mock<ILogger>();
        publicationLogger.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(publicationLogger.Object);
        var store = new Mock<IMetadataV2GraphStore>();
        store.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => realStore.GetCurrentAsync(token));
        store.Setup(provider => provider.SaveAsync(It.IsAny<MetadataV2Graph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (MetadataV2Graph graph, string? etag, CancellationToken token) =>
            {
                var saved = await realStore.SaveAsync(graph, etag, token);
                firstGraphSaved = true;
                if (concurrentChange && graph.Services.Any(service => service.Metadata.Id == target.ServiceId))
                {
                    await realStore.SaveAsync(MetadataV2FolderMutations.Create(saved.Graph, concurrentFolder), saved.Etag, token);
                    concurrentGraphSaved = true;
                }
                return saved;
            });
        try
        {
            await fixture.ApplyGlobalSeedSqlAsync($"""
                CREATE FUNCTION "{schema}".reject_import_publication() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.table_schema = TG_ARGV[0] AND NEW.table_name = TG_ARGV[1] THEN
                        RAISE EXCEPTION 'Publication commit rejected by the test' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER "{trigger}" AFTER INSERT ON honua.layers
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                EXECUTE FUNCTION "{schema}".reject_import_publication('{schema}', 'failed_parcels');
                """);
            var importer = CreateService(store.Object, schema, false,
                handler: new SubtypeFeatureServerHandler(false, false, false, false, false,
                    "/arcgis/rest/services/Survey/Parcels/FeatureServer"),
                publicationLogger: loggerFactory.Object.CreateLogger<GeoservicesLayerPublicationService>());
            var result = await importer.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = sourceUrl, LayerId = 0, TableName = "failed_parcels", TargetSchema = schema,
                BatchSize = 10, MaxRetries = 0, ServiceName = target.ServiceName, Target = target
            });
            result.DataCommitted.Should().BeTrue(result.ErrorMessage);
            result.PublishedLayerId.Should().BeNull();
            var exceptions = publicationLogger.Invocations.Where(call => call.Method.Name == "Log")
                .Select(call => call.Arguments[3] as Exception).OfType<Exception>();
            var diagnostic = string.Join("; ", result.Warnings.Concat(exceptions.Select(error => error.ToString())));
            firstGraphSaved.Should().BeTrue(diagnostic);
            concurrentGraphSaved.Should().Be(concurrentChange, diagnostic);
            store.Verify(provider => provider.SaveAsync(It.IsAny<MetadataV2Graph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2), diagnostic);
            var after = await realStore.GetCurrentAsync();
            after.Graph.Services.Should().NotContain(service => service.Metadata.Id == target.ServiceId);
            after.Graph.Folders.Should().ContainSingle(folder => folder.Metadata.Id == target.FolderId).Which.Deleted.Should().BeTrue();
            after.Graph.Services.Select(service => service.Metadata.Id).Should().Contain(initial.Graph.Services.Select(service => service.Metadata.Id));
            after.Graph.Folders.Any(folder => folder.Metadata.Id == concurrentFolder.Metadata.Id && !folder.Deleted).Should().Be(concurrentChange);
        }
        finally
        {
            await fixture.ApplyGlobalSeedSqlAsync($"DROP TRIGGER IF EXISTS \"{trigger}\" ON honua.layers;");
            await CleanupCatalogAsync(target.ServiceName);
            await fixture.DropSchemaAsync(schema);
        }
    }
}
