// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Integration coverage for issue #1379: the Validating phase invokes
/// <see cref="MigrationCatalogReconciler"/> against the published Metadata v2 catalog entry that the
/// AutoPublish step materialized, and binds the resulting catalog-parity report to the per-layer
/// <see cref="MigrationReconciliationArtifact"/>. A faithfully imported layer must yield an all-green
/// catalog reconciliation (no fail findings, <c>pass</c> classification).
/// </summary>
[Collection("Database")]
public sealed class GeoservicesImportCatalogReconciliationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WhenLayerImportedFaithfully_ProducesAllGreenCatalogReconciliation()
    {
        const string tableName = "geoservices_catalog_recon";
        var serviceName = $"catrecon_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportCatalogRecon");
        var environment = $"CatalogReconTest-{Guid.NewGuid():N}";

        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment,
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName);

            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Faithful/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName,
                ImportAttachments = false
            });

            result.Success.Should().BeTrue();
            result.NeedsReview.Should().BeFalse();

            // #1379: the catalog reconciliation report must be produced and bound to the artifact.
            result.ReconciliationArtifact.Should().NotBeNull();
            var catalog = result.ReconciliationArtifact!.CatalogReconciliation;
            catalog.Should().NotBeNull("the Validating phase must run the catalog reconciler against the published entry");

            // The report is also surfaced as a sibling on the result for the scorecard/gate path.
            result.CatalogReconciliationReport.Should().BeSameAs(catalog);

            catalog!.Resources.Should().ContainSingle();
            var resource = catalog.Resources[0];
            resource.Classification.Should().Be(
                MigrationCatalogReconciliationClassifications.Pass,
                "a faithfully imported layer must reconcile all-green; findings: {0}",
                string.Join("; ", resource.Findings.Select(f => $"{f.Code}:{f.Summary}")));
            resource.Findings.Should().BeEmpty();
            catalog.Summary.FailResourceCount.Should().Be(0);
            catalog.Summary.PassResourceCount.Should().Be(1);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportLayerAsync_PlannedReprojection_ValidatesActualTargetRows(bool displaced)
    {
        const string tableName = "reprojected_catalog_recon";
        var serviceName = $"reproject_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ReprojectCatalogRecon");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var provider = new FixtureConnectionProvider(fixture);
        var graphStore = new PostgresMetadataV2GraphStore(provider, $"Reproject-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "reprojected", Name = "Reprojected" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom"
            },
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new() { Name = "name", Type = MetadataV2FieldType.String },
                new() { Name = "status", Type = MetadataV2FieldType.String }
            ]
        };
        var reader = new PostgresStorageMappedFeatureReader(provider,
            new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy()),
            resource, new FeatureStorageMapping(TableName: tableName, SchemaName: schemaName,
                PrimaryKeyColumn: "objectid", GeometryColumn: "geom", StorageSrid: 4326),
            connection: null, connectionEncryptionService: null);
        var reconciliation = new LayerReconciliationService(reader, TimeProvider.System,
            NullLogger<LayerReconciliationService>.Instance);
        try
        {
            var service = CreateService(graphStore, schemaName,
                new FaithfulFeatureServerHandler(reprojected: true, displaced), reconciliation);
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Faithful/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName,
                ImportAttachments = false
            });
            result.Success.Should().Be(!displaced);
            result.NeedsReview.Should().Be(displaced);
            result.ReconciliationArtifact.Should().NotBeNull();
            var artifact = result.ReconciliationArtifact!;
            var extent = artifact.Layers.Should().ContainSingle().Subject.Extent;
            extent.Source!.Value.Srid.Should().Be(3857);
            extent.Target!.Value.Srid.Should().Be(4326);
            extent.ComparisonTarget!.Value.Srid.Should().Be(3857);
            extent.Classification.Should().Be(displaced ? "fail" : "pass");
            // Catalog bounds agree with the imported rows even in the corrupt case;
            // source parity must therefore be decided by the independent data gate.
            artifact.CatalogReconciliation!.Resources.Should().ContainSingle().Subject.Classification.Should().Be("pass");
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private GeoservicesImportService CreateService(PostgresMetadataV2GraphStore graphStore, string dataSchema,
        HttpMessageHandler? handler = null, ILayerReconciliationService? reconciliation = null)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler ?? new FaithfulFeatureServerHandler()),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);
        var connectionProvider = new FixtureConnectionProvider(fixture);

        var schemaConfiguration = new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            dataSchema,
            [dataSchema, "public"]);

        var publishingService = new PostgreSqlLayerPublishingService(
            new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaContext: null,
                schemaConfiguration: schemaConfiguration),
            graphStore,
            NullLogger<PostgreSqlLayerPublishingService>.Instance);

        return new GeoservicesImportService(
            restClient,
            connectionProvider,
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(
                NullLogger<GeoservicesLayerPublicationService>.Instance,
                layerPublishingService: publishingService,
                // A pass-through data-reconciliation service so the gate reaches the catalog pass; the
                // catalog reconciler reads the published entry back through the real graph store.
                reconciliationService: reconciliation ?? new PassThroughReconciliationService(),
                metadataGraphStore: graphStore));
    }

    private sealed class PassThroughReconciliationService : ILayerReconciliationService
    {
        public Task<MigrationReconciliationArtifact> ReconcileAsync(
            LayerReconciliationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MigrationReconciliationArtifact
            {
                RunId = request.RunId,
                SourceKind = request.SourceKind,
                Classification = MigrationReconciliationClassifications.Pass,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Summary = new MigrationReconciliationSummary { LayerCount = 1, PassCount = 1 },
                Layers = [],
                Reasons = [],
                Options = new LayerReconciliationOptions()
            });
    }

    private async Task EnsureCatalogSchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "base-schema.sql"));
        // honua-server#1568 (signature 2): base-schema.sql creates/ALTERs the literal,
        // process-global honua.* catalog tables (search_path isolation does not scope them), so
        // apply it under the shared seed advisory lock to keep parallel [Collection("Database")]
        // tests off the 40P01 deadlock path. The seed's own SET search_path stays first.
        await fixture.ApplyGlobalSeedSqlAsync($"SET search_path TO honua, public;\n{sql}");
    }

    private async Task CleanupCatalogAsync(string serviceName)
    {
        // #2020: these DELETEs mutate the literal, process-global honua.* catalog
        // tables (search_path isolation does not scope them), so route them through the
        // shared seed advisory lock to keep parallel [Collection("Database")] tests off
        // the 40P01 deadlock path.
        const string sql = """
            DELETE FROM honua.layer_fields
            WHERE layer_id IN (
                SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
            DELETE FROM honua.service_layers WHERE service_name = @serviceName;
            DELETE FROM honua.services WHERE service_name = @serviceName;
            """;
        await fixture.ApplyGlobalSeedSqlAsync(sql, command =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "serviceName";
            parameter.Value = serviceName;
            command.Parameters.Add(parameter);
        });
    }

    // Minimal faithful ArcGIS FeatureServer mock: a point layer in WKID 4326 with an OBJECTID, a
    // string attribute, and a small coded-value domain. No subtypes, no attachments — everything the
    // catalog reconciler probes (fields, types, geometry, SRID, identifier, domain) maps cleanly.
    private sealed class FaithfulFeatureServerHandler(bool reprojected = false, bool displaced = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

            var payload = pathAndQuery switch
            {
                "/arcgis/rest/services/Faithful/FeatureServer/0?f=json" => """
                    {
                      "id": 0,
                      "name": "Faithful Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": false,
                      "extent": { "xmin": -158, "ymin": 21, "xmax": -157, "ymax": 22, "spatialReference": { "wkid": 4326 } },
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "Name", "type": "esriFieldTypeString", "nullable": true },
                        {
                          "name": "Status",
                          "type": "esriFieldTypeString",
                          "nullable": true,
                          "domain": {
                            "type": "codedValue",
                            "name": "StatusDomain",
                            "codedValues": [
                              { "name": "Open", "code": "O" },
                              { "name": "Closed", "code": "C" }
                            ]
                          }
                        }
                      ]
                    }
                    """,
                "/arcgis/rest/services/Faithful/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" => """{"count":1}""",
                _ when pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal) => """
                    {
                      "features": [
                        {
                          "attributes": { "OBJECTID": 1, "Name": "Alpha", "Status": "O" },
                          "geometry": { "x": -157.1, "y": 21.3 }
                        }
                      ],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                _ when pathAndQuery.Contains("resultOffset=", StringComparison.Ordinal) => """
                    {
                      "features": [],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}")
            };

            if (reprojected)
            {
                var document = JsonNode.Parse(payload)!;
                if (document["extent"] is not null)
                {
                    // Independent known Web Mercator coordinates for (0,0) and (1,1).
                    document["extent"] = JsonNode.Parse("""{"xmin":0,"ymin":0,"xmax":111319.49079327357,"ymax":111325.1428663851,"spatialReference":{"wkid":3857}}""");
                }
                if (document["count"] is not null)
                {
                    document["count"] = 2;
                }
                if (pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal))
                {
                    pathAndQuery.Should().Contain("outSR=4326");
                    document["features"] = JsonNode.Parse("""[{"attributes":{"OBJECTID":1,"Name":"Alpha","Status":"O"},"geometry":{"x":0,"y":0}},{"attributes":{"OBJECTID":2,"Name":"Beta","Status":"C"},"geometry":{"x":1,"y":1}}]""");
                    if (displaced)
                    {
                        document["features"]![1]!["geometry"]!["x"] = 10;
                    }
                }
                payload = document.ToJsonString();
            }

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString()
            => new Npgsql.NpgsqlConnectionStringBuilder(postgresFixture.ConnectionString)
            {
                SearchPath = "honua,public"
            }.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await postgresFixture.DataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
