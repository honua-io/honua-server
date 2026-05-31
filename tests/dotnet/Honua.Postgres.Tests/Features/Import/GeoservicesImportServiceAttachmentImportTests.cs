// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class GeoservicesImportServiceAttachmentImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WhenLayerAdvertisesAttachments_CopiesAttachmentsIntoStoreAndReportsCounts()
    {
        const string tableName = "geoservices_attachment_import";
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesImportServiceAttachmentImportTests));
        var handler = new AttachmentFeatureServerHandler();
        var attachmentStore = new RecordingAttachmentStore();
        var publishingService = new StubLayerPublishingService(publishedLayerId: 42);
        var service = CreateService(handler, attachmentStore, publishingService);

        try
        {
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
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
                ServiceName = "default"
            });

            result.Success.Should().BeTrue();
            result.FeatureCount.Should().Be(2);
            result.PublishedLayerId.Should().Be(42);
            result.AttachmentCount.Should().Be(3);
            result.FailedAttachments.Should().Be(0);

            attachmentStore.Uploaded.Should().HaveCount(3);
            attachmentStore.Uploaded.Select(static a => a.LayerId).Should().AllBeEquivalentTo(42);
            attachmentStore.Uploaded.Select(static a => a.Filename).Should()
                .BeEquivalentTo("photo1.jpg", "photo2.jpg", "notes.txt");

            // Source OBJECTID 1 (Alpha) -> 2 attachments, OBJECTID 2 (Beta) -> 1 attachment.
            var honuaFeatureIds = attachmentStore.Uploaded
                .Select(static a => a.FeatureId)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray();
            honuaFeatureIds.Should().HaveCount(2);
            attachmentStore.Uploaded
                .Where(a => a.FeatureId == honuaFeatureIds[0])
                .Select(static a => a.Filename)
                .OrderBy(static n => n)
                .Should()
                .BeEquivalentTo(["notes.txt", "photo1.jpg"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_WhenAttachmentDownloadFails_CountsFailureAndContinues()
    {
        const string tableName = "geoservices_attachment_partial";
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(GeoservicesImportServiceAttachmentImportTests) + "_Failure");
        var handler = new AttachmentFeatureServerHandler(failAttachmentId: 1001);
        var attachmentStore = new RecordingAttachmentStore();
        var publishingService = new StubLayerPublishingService(publishedLayerId: 7);
        var service = CreateService(handler, attachmentStore, publishingService);

        try
        {
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
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
                ServiceName = "default"
            });

            result.Success.Should().BeTrue();
            result.AttachmentCount.Should().Be(2);
            result.FailedAttachments.Should().Be(1);
            result.Warnings.Should().Contain(static w => w.Contains("failure", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_WhenImportAttachmentsDisabled_SkipsAttachmentCopy()
    {
        const string tableName = "geoservices_attachment_disabled";
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(GeoservicesImportServiceAttachmentImportTests) + "_Disabled");
        var handler = new AttachmentFeatureServerHandler();
        var attachmentStore = new RecordingAttachmentStore();
        var publishingService = new StubLayerPublishingService(publishedLayerId: 9);
        var service = CreateService(handler, attachmentStore, publishingService);

        try
        {
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
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
            });

            result.Success.Should().BeTrue();
            result.AttachmentCount.Should().Be(0);
            result.FailedAttachments.Should().Be(0);
            attachmentStore.Uploaded.Should().BeEmpty();
            handler.AttachmentRequestPaths.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private GeoservicesImportService CreateService(
        HttpMessageHandler handler,
        IAttachmentStore attachmentStore,
        ILayerPublishingService publishingService)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(4326, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>(new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/4326",
                4326,
                AxisOrder.EastNorth,
                true)));

        return new GeoservicesImportService(
            restClient,
            new FixtureConnectionProvider(fixture),
            crsRegistry.Object,
            NullLogger<GeoservicesImportService>.Instance,
            layerPublishingService: publishingService,
            attachmentStore: attachmentStore);
    }

    private sealed class RecordingAttachmentStore : IAttachmentStore
    {
        public List<UploadedAttachment> Uploaded { get; } = [];
        private long _nextId = 1;

        public async Task<Honua.Core.Features.Attachments.Domain.Attachment> UploadAsync(
            int layerId,
            long featureId,
            string filename,
            string contentType,
            Stream content,
            string? keywords = null,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var id = _nextId++;
            Uploaded.Add(new UploadedAttachment(layerId, featureId, filename, contentType, bytes, keywords));
            return Honua.Core.Features.Attachments.Domain.Attachment.CreateForUpload(
                id,
                featureId,
                layerId,
                filename,
                contentType,
                bytes.LongLength,
                $"test/{layerId}/{featureId}/{Guid.NewGuid():N}",
                keywords);
        }

        public Task<Honua.Core.Features.Attachments.Domain.Attachment?> GetAsync(
            int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<Honua.Core.Features.Attachments.Domain.Attachment?>(null);

        public Task<Honua.Core.Features.Attachments.Domain.Attachment[]> ListAsync(
            int layerId, long featureId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Honua.Core.Features.Attachments.Domain.Attachment>());

        public Task<Honua.Core.Features.Attachments.Domain.Attachment> CreateAsync(
            int layerId,
            long featureId,
            Honua.Core.Features.Attachments.Domain.Attachment attachment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Honua.Core.Features.Attachments.Domain.Attachment> UpdateAsync(
            int layerId,
            long featureId,
            Honua.Core.Features.Attachments.Domain.Attachment attachment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Honua.Core.Features.Attachments.Domain.Attachment> ReplaceAsync(
            int layerId,
            long featureId,
            long attachmentId,
            string filename,
            string contentType,
            Stream content,
            string? keywords = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Honua.Core.Features.Attachments.Domain.AttachmentContent?> DownloadAsync(
            int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<Honua.Core.Features.Attachments.Domain.AttachmentContent?>(null);

        public sealed record UploadedAttachment(
            int LayerId,
            long FeatureId,
            string Filename,
            string ContentType,
            byte[] Content,
            string? Keywords);
    }

    private sealed class StubLayerPublishingService(int publishedLayerId) : ILayerPublishingService
    {
        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString, string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString,
            LayerPublishRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PublishedLayerSummary
            {
                LayerId = publishedLayerId,
                LayerName = request.LayerName,
                Schema = request.Schema,
                Table = request.Table,
                Description = request.Description,
                GeometryType = request.GeometryType ?? "Point",
                Srid = request.Srid ?? 4326,
                PrimaryKey = request.PrimaryKey,
                FieldCount = 2,
                Enabled = true,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(
            string connectionString,
            TablePublishValidationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = request.Schema,
                Table = request.Table,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<LayerExtentRefreshResult?>(null);
    }

    private sealed class AttachmentFeatureServerHandler(long? failAttachmentId = null) : HttpMessageHandler
    {
        public List<string> AttachmentRequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

            // Attachment paths
            if (pathAndQuery.Contains("/attachments/", StringComparison.Ordinal))
            {
                AttachmentRequestPaths.Add(pathAndQuery);
                if (failAttachmentId.HasValue && pathAndQuery.EndsWith($"/{failAttachmentId.Value}", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("binary-payload"))
                };
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return Task.FromResult(resp);
            }

            if (pathAndQuery.Contains("/queryAttachments", StringComparison.Ordinal))
            {
                AttachmentRequestPaths.Add(pathAndQuery);
                return Task.FromResult(JsonResponse("""
                    {
                      "attachmentGroups": [
                        {
                          "parentObjectId": 1,
                          "attachmentInfos": [
                            { "id": 1001, "name": "photo1.jpg", "contentType": "image/jpeg", "size": 14 },
                            { "id": 1002, "name": "notes.txt", "contentType": "text/plain", "size": 14 }
                          ]
                        },
                        {
                          "parentObjectId": 2,
                          "attachmentInfos": [
                            { "id": 1003, "name": "photo2.jpg", "contentType": "image/jpeg", "size": 14 }
                          ]
                        }
                      ]
                    }
                    """));
            }

            return pathAndQuery switch
            {
                "/arcgis/rest/services/Inspections/FeatureServer/0?f=json" => Task.FromResult(JsonResponse("""
                    {
                      "id": 0,
                      "name": "Inspections",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": true,
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
                        {
                          "features": [],
                          "exceededTransferLimit": false,
                          "spatialReference": { "wkid": 4326 }
                        }
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
