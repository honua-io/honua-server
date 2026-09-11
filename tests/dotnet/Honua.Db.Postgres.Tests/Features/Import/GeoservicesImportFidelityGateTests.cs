// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
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
/// Issue #4600, acceptance criterion 3: catalog discrepancies must control the completion verdict.
/// These run end to end against real PostGIS and the real Metadata v2 graph store — the import
/// publishes, the publish materializes the catalog entry, and the catalog reconciler reads it back.
/// </summary>
/// <remarks>
/// The source layer here advertises an Esri subtype set keyed on <c>assetclass</c>, a field the
/// service never lists in its <c>fields</c> array. The importer therefore cannot create that column,
/// the publish path prunes the subtype set, and the migrated layer silently loses a construct the
/// source had. Every feature still lands, so data-movement reconciliation is green: only the
/// catalog pass can see the loss. Before #4600 that pass was recorded and then ignored, and the run
/// reported <c>Completed</c>.
/// </remarks>
[Collection("Database")]
public sealed class GeoservicesImportFidelityGateTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WhenCatalogReconciliationFails_RoutesToNeedsReviewDespiteGreenDataReconciliation()
    {
        const string tableName = "geoservices_fidelity_gate";
        var serviceName = $"fidgate_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportFidelityGate");
        var environment = $"FidelityGateTest-{Guid.NewGuid():N}";

        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment,
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName);

            var result = await service.ImportLayerAsync(BuildRequest(tableName, schemaName, serviceName));

            // The data moved: the single source feature is in the target table and the
            // data-movement reconciliation probe is green. A count-only gate sees nothing wrong.
            result.FeatureCount.Should().Be(1);
            result.FailedFeatures.Should().Be(0);
            result.ReconciliationArtifact.Should().NotBeNull();
            result.ReconciliationArtifact!.Classification.Should().Be(MigrationReconciliationClassifications.Pass);

            // The catalog pass sees the dropped subtype set.
            var catalog = result.CatalogReconciliationReport;
            catalog.Should().NotBeNull();
            catalog!.Summary.FailResourceCount.Should().Be(1);
            catalog.Resources.Should().ContainSingle()
                .Which.Findings.Should().Contain(finding =>
                    finding.Code == MigrationCatalogReconciliationCodes.SubtypeMissing &&
                    finding.Severity == MigrationCatalogReconciliationSeverities.Fail);

            // #4600: that catalog finding now controls the verdict.
            result.Success.Should().BeFalse("a migration that dropped the source subtype set is not a successful migration");
            result.NeedsReview.Should().BeTrue();
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

            var difference = result.FidelityDifferences
                .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.CatalogReconciliationFailed)
                .Subject;
            difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
            difference.Subject.Should().EndWith(":subtypes");
            difference.Summary.Should().Contain(MigrationCatalogReconciliationCodes.SubtypeMissing);

            result.ErrorMessage.Should().Contain(MigrationCatalogReconciliationCodes.SubtypeMissing);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// The complementary case: when the same pipeline reconciles clean on both passes the run
    /// reports <see cref="MigrationFidelityVerdicts.FullFidelity"/>. Without this the gate could be
    /// "green" simply by never letting anything through.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenEveryCheckRanAndPassed_ReportsFullFidelity()
    {
        const string tableName = "geoservices_fidelity_full";
        var serviceName = $"fidfull_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportFidelityFull");
        var environment = $"FidelityFullTest-{Guid.NewGuid():N}";

        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment,
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName, faithful: true);

            var result = await service.ImportLayerAsync(BuildRequest(tableName, schemaName, serviceName, faithful: true));

            result.Success.Should().BeTrue();
            result.NeedsReview.Should().BeFalse();
            result.FidelityVerdict.Should().Be(
                MigrationFidelityVerdicts.FullFidelity,
                "every required check ran and passed; differences: {0}",
                string.Join("; ", result.FidelityDifferences.Select(d => $"{d.Code}:{d.Summary}")));
            result.FidelityDifferences.Should().BeEmpty();
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// A deployment without the catalog read-back seam cannot prove schema parity, so the run must
    /// report <see cref="MigrationFidelityVerdicts.Unverified"/> rather than full fidelity — while
    /// still completing, because an unexecuted check is not evidence of a faithless import.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_WhenCatalogCheckCannotRun_CompletesAsUnverifiedNotFullFidelity()
    {
        const string tableName = "geoservices_fidelity_unverified";
        var serviceName = $"fidunv_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportFidelityUnverified");

        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            $"FidelityUnverifiedTest-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            // No metadataGraphStore is handed to the publication service, so the catalog read-back
            // seam is absent and the catalog pass never executes.
            var service = CreateService(graphStore, schemaName, faithful: true, catalogReadBack: false);

            var result = await service.ImportLayerAsync(BuildRequest(tableName, schemaName, serviceName, faithful: true));

            result.Success.Should().BeTrue("an unexecuted check is not evidence of a faithless import");
            result.NeedsReview.Should().BeFalse();
            result.CatalogReconciliationReport.Should().BeNull();
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Unverified);

            var difference = result.FidelityDifferences
                .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted)
                .Subject;
            difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Unverified);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private static GeoservicesImportRequest BuildRequest(
        string tableName,
        string schemaName,
        string serviceName,
        bool faithful = false) => new()
        {
            ServiceUrl = faithful
                ? "https://example.com/arcgis/rest/services/FidelityFaithful/FeatureServer"
                : "https://example.com/arcgis/rest/services/FidelityDrop/FeatureServer",
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
        };

    private GeoservicesImportService CreateService(
        PostgresMetadataV2GraphStore graphStore,
        string dataSchema,
        bool faithful = false,
        bool catalogReadBack = true)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(new FidelityFeatureServerHandler(faithful)),
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
                // A pass-through data-movement service keeps that probe green, so the assertions
                // isolate what the catalog pass contributes to the verdict.
                reconciliationService: new PassThroughReconciliationService(),
                metadataGraphStore: catalogReadBack ? graphStore : null));
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
        // base-schema.sql creates/ALTERs the literal, process-global honua.* catalog tables
        // (search_path isolation does not scope them), so apply it under the shared seed advisory
        // lock to keep parallel [Collection("Database")] tests off the 40P01 deadlock path.
        await fixture.ApplyGlobalSeedSqlAsync($"SET search_path TO honua, public;\n{sql}");
    }

    private async Task CleanupCatalogAsync(string serviceName)
    {
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

    /// <summary>
    /// ArcGIS FeatureServer mock with two shapes. The default ("drop") layer advertises a subtype
    /// set keyed on <c>assetclass</c>, a field the service never lists, so the construct cannot
    /// survive the import. The faithful layer advertises the same subtype set keyed on a field it
    /// does publish.
    /// </summary>
    private sealed class FidelityFeatureServerHandler(bool faithful) : HttpMessageHandler
    {
        private const string SubtypeBlockDrop = """
                      "subtypeField": "assetclass",
                      "defaultSubtypeCode": 1,
                      "subtypes": [
                        { "code": 1, "name": "Hydrant" },
                        { "code": 2, "name": "Valve" }
                      ],
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "label", "type": "esriFieldTypeString", "nullable": true }
                      ]
            """;

        private const string SubtypeBlockFaithful = """
                      "subtypeField": "assetclass",
                      "defaultSubtypeCode": 1,
                      "subtypes": [
                        { "code": 1, "name": "Hydrant" },
                        { "code": 2, "name": "Valve" }
                      ],
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "assetclass", "type": "esriFieldTypeInteger", "nullable": true },
                        { "name": "label", "type": "esriFieldTypeString", "nullable": true }
                      ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            var attributes = faithful
                ? """{ "OBJECTID": 1, "assetclass": 2, "label": "Main St" }"""
                : """{ "OBJECTID": 1, "label": "Main St" }""";

            string payload;
            if (pathAndQuery.EndsWith("/FeatureServer/0?f=json", StringComparison.Ordinal))
            {
                payload = $$"""
                    {
                      "id": 0,
                      "name": "Fidelity Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": false,
                      "extent": { "xmin": -158, "ymin": 21, "xmax": -157, "ymax": 22, "spatialReference": { "wkid": 4326 } },
                    {{(faithful ? SubtypeBlockFaithful : SubtypeBlockDrop)}}
                    }
                    """;
            }
            else if (pathAndQuery.Contains("returnCountOnly=true", StringComparison.Ordinal))
            {
                payload = """{"count":1}""";
            }
            else if (pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal))
            {
                payload = $$"""
                    {
                      "features": [
                        {
                          "attributes": {{attributes}},
                          "geometry": { "x": -157.1, "y": 21.3 }
                        }
                      ],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """;
            }
            else if (pathAndQuery.Contains("resultOffset=", StringComparison.Ordinal))
            {
                payload = """
                    {
                      "features": [],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}");
            }

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(
                new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
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
