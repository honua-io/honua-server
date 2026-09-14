// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportSubtypePersistenceTests
{
    [Fact]
    public async Task ImportLayerAsync_WithNonSpatialTable_PublishesQueryableRowsWithoutGeometry()
    {
        const string tableName = "geoservices_summary_table";
        var serviceName = $"summary_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportSummary");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture), $"Summary-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName, false, nonSpatial: true);
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Subtypes/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName
            });

            result.Success.Should().BeTrue();
            result.Warnings.Should().NotContain(w => w.Contains("publishing did not complete", StringComparison.OrdinalIgnoreCase));
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources.Single(r => r.Metadata.Name == "Subtype Layer");
            resource.Type.Should().Be(MetadataV2ResourceType.Table);
            resource.Spatial.Should().BeNull();
            snapshot.Graph.Publications.Should().NotContain(p =>
                p.ResourceId == resource.Metadata.Id && p.PublicationType == MetadataV2PublicationType.StacCollection);
            resource.SchemaFields.Should().NotContain(f => f.Type == MetadataV2FieldType.Geometry);
            resource.Subtypes.Should().NotBeNull();
            var storage = snapshot.Graph.StorageBindings.Single(b => b.ResourceId == resource.Metadata.Id);
            var mapping = FeatureStorageMapping.FromMetadata(resource, storage);
            mapping.GeometryColumn.Should().BeNull();
            mapping.StorageSrid.Should().BeNull();

            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
            var reader = new PostgresStorageMappedFeatureReader(
                new FixtureConnectionProvider(fixture), pool, resource, mapping,
                connection: null, connectionEncryptionService: null);
            var layerId = storage.StorageLayerId ?? throw new InvalidOperationException("Published table has no layer identity");
            var all = await reader.QueryAsync(layerId, new FeatureQuery { OrderBy = [OrderByClause.Asc("objectid")] });
            all.Items.Should().HaveCount(2);
            all.Items.Should().OnlyContain(row => row.Geometry == null);
            all.Items.Select(row => row.Attributes["join_id"]).Should().Equal("001-A", "002-B");
            all.Items[0].Attributes["status"].Should().BeNull();
            all.Items[1].Attributes["status"].Should().Be("occupied");
            var page = await reader.QueryAsync(layerId, new FeatureQuery
            {
                OrderBy = [OrderByClause.Asc("objectid")],
                Offset = 1,
                Limit = 1
            });
            page.Items.Should().ContainSingle().Which.Attributes["join_id"].Should().Be("002-B");

            var reconciliation = new LayerReconciliationService(reader, TimeProvider.System,
                NullLogger<LayerReconciliationService>.Instance);
            var report = await reconciliation.ReconcileAsync(new LayerReconciliationRequest
            {
                RunId = "attribute-only-import", SourceKind = "arcgis-geoservices-rest",
                Layers = [new LayerReconciliationLayerInput
                {
                    SourceLayerId = "source#0", TargetHonuaLayerId = layerId,
                    SourceFeatureCount = 2, SourceHasGeometry = false,
                    SourceFieldNames = ["join_id", "status"]
                }]
            });
            report.Classification.Should().Be(MigrationReconciliationClassifications.Pass);
            report.Layers[0].Geometry.Sampled.Should().Be(0);
            report.Layers[0].Geometry.Reason.Should().Contain("not applicable");

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table AND udt_name IN ('geometry', 'geography')";
            command.Parameters.AddWithValue("schema", schemaName);
            command.Parameters.AddWithValue("table", tableName);
            (await command.ExecuteScalarAsync()).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = @schema AND tablename = @table AND indexdef ILIKE '%USING gist%'";
            (await command.ExecuteScalarAsync()).Should().Be(0L);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }
}
