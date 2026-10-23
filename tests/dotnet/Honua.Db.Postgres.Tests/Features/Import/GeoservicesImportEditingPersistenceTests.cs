// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportSubtypePersistenceTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ImportLayerAsync_PreservesEditingIdentityWithoutGrantingWriteAccess(
        bool importAttachments, bool attachmentStoreAvailable, bool declaresGlobalIdField)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportEditing");
        var serviceName = $"editing_{Guid.NewGuid():N}";
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture), $"Editing-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        try
        {
            var handler = new EditingFeatureServerHandler(declaresGlobalIdField);
            var store = attachmentStoreAvailable ? new Mock<IAttachmentStore>().Object : null;
            var importer = CreateService(graphStore, schemaName, false, handler: handler, attachmentStore: store);
            var result = await importer.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Editing/FeatureServer",
                LayerId = 0,
                TableName = "editing_identity",
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName,
                ImportAttachments = importAttachments
            });

            result.PublishedLayerId.Should().NotBeNull();
            result.FeatureCount.Should().Be(1);
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources.Single(r => r.Metadata.Name == "Editing Layer");
            resource.Editing.Should().NotBeNull();
            resource.Editing!.GlobalIdField.Should().Be("stable_id");
            resource.Editing.SupportsAttachments.Should().Be(importAttachments && attachmentStoreAvailable);
            resource.Editing.CanModify.Should().BeFalse("source capabilities are not target edit grants");
            resource.SchemaFields.Single(f => f.Name == "stable_id").Type.Should().Be(MetadataV2FieldType.Uuid);
            var publication = snapshot.Graph.Publications.Single(p =>
                p.ResourceId == resource.Metadata.Id && p.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);
            publication.Capabilities.Should().NotContain(new[] { "Create", "Update", "Delete", "Editing" });
            handler.AttachmentQueries.Should().Be(importAttachments && attachmentStoreAvailable ? 1 : 0);

            await using var connection = await fixture.GetConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT stable_id FROM \"{schemaName}\".editing_identity";
            (await command.ExecuteScalarAsync()).Should().Be(Guid.Parse("090cfe5f-e253-4e2e-ae8c-0b8f526ff310"));
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private sealed class EditingFeatureServerHandler(bool declaresGlobalIdField) : HttpMessageHandler
    {
        public int AttachmentQueries { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.PathAndQuery;
            string payload;
            if (url.Contains("queryAttachments", StringComparison.Ordinal))
            {
                AttachmentQueries++;
                payload = """{"attachmentGroups":[]}""";
            }
            else if (url.Contains("returnCountOnly=true", StringComparison.Ordinal))
            {
                payload = """{"count":1}""";
            }
            else if (url.Contains("/query", StringComparison.Ordinal))
            {
                payload = """
                    {"features":[{"attributes":{"OBJECTID":71,"Stable-ID":"090cfe5f-e253-4e2e-ae8c-0b8f526ff310"},
                    "geometry":{"x":-157.1,"y":21.3}}],"exceededTransferLimit":false,"spatialReference":{"wkid":4326}}
                    """;
            }
            else
            {
                payload = """
                    {"id":0,"name":"Editing Layer","type":"Feature Layer","geometryType":"esriGeometryPoint",
                    "hasAttachments":true,"globalIdField":"Stable-ID","capabilities":"Query,Create,Update,Delete,Editing",
                    "maxRecordCount":10,"fields":[{"name":"OBJECTID","type":"esriFieldTypeOID","nullable":false},
                    {"name":"Stable-ID","type":"esriFieldTypeGlobalID","nullable":false}]}
                    """;
                if (!declaresGlobalIdField)
                {
                    payload = payload.Replace("\"globalIdField\":\"Stable-ID\",", string.Empty, StringComparison.Ordinal);
                }
            }
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
