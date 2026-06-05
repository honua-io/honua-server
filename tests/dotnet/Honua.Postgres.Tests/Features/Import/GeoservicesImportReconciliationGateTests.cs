// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Drives the post-publish reconciliation gate wired into the Geoservices import Validating phase
/// (issues #1247/#1380). A reconciliation verdict of <c>fail</c> must route the run to
/// <see cref="GeoservicesImportStatus.NeedsReview"/> and block completion; a faithful import must
/// reach <see cref="GeoservicesImportStatus.Completed"/>.
/// </summary>
[Collection("Database")]
public sealed class GeoservicesImportReconciliationGateTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WhenReconciliationReportsFail_RoutesToNeedsReviewAndBlocksCompleted()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesImportReconciliationGateTests) + "_fail");
        var reconciliation = new StubReconciliationService(MigrationReconciliationClassifications.Fail, failCount: 1);
        var progress = new RecordingProgress();
        var service = CreateService(new SimpleFeatureServerHandler(), publishedLayerId: 100, reconciliation);

        try
        {
            var result = await service.ImportLayerAsync(BuildRequest("recon_gate_fail", schemaName), progress);

            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeTrue();
            result.ReconciliationArtifact.Should().NotBeNull();
            result.ReconciliationArtifact!.Classification.Should().Be(MigrationReconciliationClassifications.Fail);

            progress.Statuses.Should().Contain(GeoservicesImportStatus.Validating);
            progress.Statuses.Should().Contain(GeoservicesImportStatus.NeedsReview);
            progress.Statuses.Should().NotContain(GeoservicesImportStatus.Completed);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_WhenReconciliationPasses_ReachesCompleted()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesImportReconciliationGateTests) + "_pass");
        var reconciliation = new StubReconciliationService(MigrationReconciliationClassifications.Pass, failCount: 0);
        var progress = new RecordingProgress();
        var service = CreateService(new SimpleFeatureServerHandler(), publishedLayerId: 101, reconciliation);

        try
        {
            var result = await service.ImportLayerAsync(BuildRequest("recon_gate_pass", schemaName), progress);

            result.Success.Should().BeTrue();
            result.NeedsReview.Should().BeFalse();
            result.ReconciliationArtifact.Should().NotBeNull();
            result.ReconciliationArtifact!.Classification.Should().Be(MigrationReconciliationClassifications.Pass);

            progress.Statuses.Should().Contain(GeoservicesImportStatus.Validating);
            progress.Statuses.Should().Contain(GeoservicesImportStatus.Completed);
            progress.Statuses.Should().NotContain(GeoservicesImportStatus.NeedsReview);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private static GeoservicesImportRequest BuildRequest(string tableName, string schemaName) => new()
    {
        ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
        LayerId = 0,
        TableName = tableName,
        TargetSchema = schemaName,
        TargetSrid = 4326,
        BatchSize = 10,
        RequestTimeoutSeconds = 5,
        MaxRetries = 0,
        AutoPublish = true,
        ServiceName = "default",
        ImportAttachments = false
    };

    private GeoservicesImportService CreateService(
        HttpMessageHandler handler,
        int publishedLayerId,
        ILayerReconciliationService reconciliationService)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);

        return new GeoservicesImportService(
            restClient,
            new FixtureConnectionProvider(fixture),
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            layerPublishingService: new StubLayerPublishingService(publishedLayerId),
            reconciliationService: reconciliationService);
    }

    private sealed class StubReconciliationService(string classification, int failCount) : ILayerReconciliationService
    {
        public Task<MigrationReconciliationArtifact> ReconcileAsync(
            LayerReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            var artifact = new MigrationReconciliationArtifact
            {
                RunId = request.RunId,
                SourceKind = request.SourceKind,
                Classification = classification,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Summary = new MigrationReconciliationSummary
                {
                    LayerCount = 1,
                    PassCount = failCount == 0 ? 1 : 0,
                    FailCount = failCount
                },
                Layers = [],
                Reasons = failCount > 0 ? ["Injected data loss for gate test."] : [],
                Options = new LayerReconciliationOptions()
            };
            return Task.FromResult(artifact);
        }
    }

    private sealed class RecordingProgress : IProgress<GeoservicesImportProgress>
    {
        public List<GeoservicesImportStatus> Statuses { get; } = [];
        public void Report(GeoservicesImportProgress value) => Statuses.Add(value.Status);
    }

    private sealed class StubLayerPublishingService(int publishedLayerId) : ILayerPublishingService
    {
        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString, string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PublishedLayerSummary
            {
                LayerId = publishedLayerId,
                LayerName = request.LayerName,
                Schema = request.Schema,
                Table = request.Table,
                GeometryType = request.GeometryType ?? "Point",
                Srid = request.Srid ?? 4326,
                PrimaryKey = request.PrimaryKey,
                FieldCount = 2,
                Enabled = true,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
            string connectionString, int layerId, string serviceName, bool enabled, CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(
            string connectionString, TablePublishValidationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = request.Schema,
                Table = request.Table,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString, int layerId, string serviceName, bool enabled, CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString, string serviceName, bool enabled, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
            string connectionString, string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<LayerExtentRefreshResult?>(null);
    }

    private sealed class SimpleFeatureServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            return pathAndQuery switch
            {
                "/arcgis/rest/services/Inspections/FeatureServer/0?f=json" => Task.FromResult(JsonResponse("""
                    {
                      "id": 0,
                      "name": "Inspections",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": false,
                      "extent": { "xmin": -158, "ymin": 21, "xmax": -157, "ymax": 22, "spatialReference": { "wkid": 4326 } },
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "Name", "type": "esriFieldTypeString", "nullable": true }
                      ]
                    }
                    """)),
                "/arcgis/rest/services/Inspections/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" =>
                    Task.FromResult(JsonResponse("""{"count":2}""")),
                "/arcgis/rest/services/Inspections/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=0&resultRecordCount=10&outSR=4326" =>
                    Task.FromResult(JsonResponse("""
                        {
                          "features": [
                            { "attributes": { "OBJECTID": 1, "Name": "Alpha" }, "geometry": { "x": -157.1, "y": 21.3 } },
                            { "attributes": { "OBJECTID": 2, "Name": "Beta" }, "geometry": { "x": -157.2, "y": 21.4 } }
                          ],
                          "exceededTransferLimit": false,
                          "spatialReference": { "wkid": 4326 }
                        }
                        """)),
                "/arcgis/rest/services/Inspections/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=2&resultRecordCount=10&outSR=4326" =>
                    Task.FromResult(JsonResponse("""
                        { "features": [], "exceededTransferLimit": false, "spatialReference": { "wkid": 4326 } }
                        """)),
                _ => throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}")
            };
        }

        private static HttpResponseMessage JsonResponse(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => postgresFixture.ConnectionString;

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
