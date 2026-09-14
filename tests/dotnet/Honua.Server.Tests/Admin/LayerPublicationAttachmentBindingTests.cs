// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.QueryAttachments)]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task PublishedBinding_AttachmentRoundTripUsesLiveParentInsteadOfSnapshot(bool nullGeometry)
    {
        await PublishAttachmentBindingAsync();
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                ALTER TABLE public.{_tableName} ALTER COLUMN geom DROP NOT NULL;
                INSERT INTO public.{_tableName} (id, name, population, geom)
                VALUES (900001, 'Bound Parent', 321,
                    CASE WHEN @nullGeometry THEN NULL ELSE ST_SetSRID(ST_Point(2, 2), 4326) END);
                """;
            command.Parameters.AddWithValue("nullGeometry", nullGeometry);
            await command.ExecuteNonQueryAsync();
        }

        const long parentId = 900001;
        var layerUrl = $"/rest/services/{_serviceName}/FeatureServer/{_layerId}";
        var parentUrl = $"{layerUrl}/{parentId}";
        using var query = await ReadAttachmentJsonAsync($"{layerUrl}/query?objectIds={parentId}&outFields=*&returnGeometry=false&f=json");
        query.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
        using var empty = await ReadAttachmentJsonAsync($"{parentUrl}/attachments?f=json");
        empty.RootElement.GetProperty("attachmentInfos").GetArrayLength().Should().Be(0);

        long? attachmentId = null;
        var bytes = "attachment on a live bound parent"u8.ToArray();
        try
        {
            using var upload = AttachmentUploadForm(bytes);
            using var added = await SendAttachmentJsonAsync($"{parentUrl}/addAttachment", upload);
            var addResult = added.RootElement.GetProperty("addAttachmentResult");
            addResult.GetProperty("success").GetBoolean().Should().BeTrue();
            attachmentId = addResult.GetProperty("objectId").GetInt64();

            using var list = await ReadAttachmentJsonAsync($"{parentUrl}/attachments?f=json");
            list.RootElement.GetProperty("attachmentInfos").GetArrayLength().Should().Be(1);
            using var unfiltered = await ReadAttachmentJsonAsync($"{layerUrl}/queryAttachments?objectIds={parentId}&f=json");
            unfiltered.RootElement.GetProperty("attachmentGroups")[0].GetProperty("parentObjectId").GetInt64().Should().Be(parentId);
            unfiltered.RootElement.GetProperty("attachmentInfos")[0].GetProperty("id").GetInt64().Should().Be(attachmentId.Value);
            using var filtered = await ReadAttachmentJsonAsync(
                $"{layerUrl}/queryAttachments?objectIds={parentId}&definitionExpression=name%3D%27Bound%20Parent%27&f=json");
            filtered.RootElement.GetProperty("attachmentInfos").GetArrayLength().Should().Be(1);
            using var excluded = await ReadAttachmentJsonAsync(
                $"{layerUrl}/queryAttachments?objectIds={parentId}&definitionExpression=name%3D%27Wrong%20Default%20Store%27&f=json");
            excluded.RootElement.GetProperty("attachmentGroups").GetArrayLength().Should().Be(0);
            (await _client.GetByteArrayAsync($"{parentUrl}/attachments/{attachmentId}")).Should().Equal(bytes);

            using var updateForm = new MultipartFormDataContent
            {
                { new StringContent(attachmentId.Value.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
                { new StringContent("updated-bound.pdf"), "name" }
            };
            using var updated = await SendAttachmentJsonAsync($"{parentUrl}/updateAttachment", updateForm);
            updated.RootElement.GetProperty("updateAttachmentResult").GetProperty("success").GetBoolean().Should().BeTrue();
            using var renamed = await ReadAttachmentJsonAsync($"{parentUrl}/attachments?f=json");
            renamed.RootElement.GetProperty("attachmentInfos")[0].GetProperty("name").GetString().Should().Be("updated-bound.pdf");
            using var deleteForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["attachmentIds"] = attachmentId.Value.ToString(CultureInfo.InvariantCulture)
            });
            using var deleted = await SendAttachmentJsonAsync($"{parentUrl}/deleteAttachments", deleteForm);
            deleted.RootElement.GetProperty("deleteAttachmentResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
            using var final = await ReadAttachmentJsonAsync($"{parentUrl}/attachments?f=json");
            final.RootElement.GetProperty("attachmentInfos").GetArrayLength().Should().Be(0);
        }
        finally
        {
            if (attachmentId.HasValue)
            {
                await _fixture.GetService<IAttachmentStore>().DeleteAsync(_layerId!.Value, parentId, attachmentId.Value);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task PublishedBinding_AttachmentsDoNotExposeParentRemainingOnlyInOldSnapshot()
    {
        await PublishAttachmentBindingAsync();
        var store = _fixture.GetService<IAttachmentStore>();
        await using var content = new MemoryStream("private parent attachment"u8.ToArray());
        var attachment = await store.UploadAsync(_layerId!.Value, 1, "parent.txt", "text/plain", content);
        var layerUrl = $"/rest/services/{_serviceName}/FeatureServer/{_layerId}";
        var parentUrl = $"{layerUrl}/1";
        try
        {
            // Publication materialized the original row into the legacy snapshot.
            // Removing it only from the bound table must revoke attachment visibility.
            await using (var connection = await _fixture.Postgres.GetConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"DELETE FROM public.{_tableName} WHERE id = 1";
                (await command.ExecuteNonQueryAsync()).Should().Be(1);
            }

            (await _fixture.GetService<IFeatureReader>().GetAsync(_layerId.Value, 1))
                .Should().NotBeNull("the regression requires a stale row in the default store");
            using var query = await ReadAttachmentJsonAsync($"{layerUrl}/query?objectIds=1&outFields=*&f=json");
            query.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
            using var batch = await ReadAttachmentJsonAsync($"{layerUrl}/queryAttachments?objectIds=1&f=json");
            batch.RootElement.GetProperty("attachmentGroups").GetArrayLength().Should().Be(0);
            using var listed = await ReadAttachmentJsonAsync($"{parentUrl}/attachments?f=json", allowError: true);
            listed.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            using var download = await ReadAttachmentJsonAsync($"{parentUrl}/attachments/{attachment.Id}", allowError: true);
            download.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            using var upload = AttachmentUploadForm("must not be stored"u8.ToArray());
            using var deniedAdd = await SendAttachmentJsonAsync($"{parentUrl}/addAttachment", upload, allowError: true);
            deniedAdd.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            using var update = new MultipartFormDataContent
            {
                { new StringContent(attachment.Id.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
                { new StringContent("must-not-rename.pdf"), "name" }
            };
            using var deniedUpdate = await SendAttachmentJsonAsync($"{parentUrl}/updateAttachment", update, allowError: true);
            deniedUpdate.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["attachmentIds"] = attachment.Id.ToString(CultureInfo.InvariantCulture)
            });
            using var deniedDelete = await SendAttachmentJsonAsync($"{parentUrl}/deleteAttachments", form, allowError: true);
            deniedDelete.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            var retained = await store.GetAsync(_layerId.Value, 1, attachment.Id);
            retained.Should().NotBeNull();
            retained!.Value.Filename.Should().Be("parent.txt");
        }
        finally
        {
            await store.DeleteAsync(_layerId.Value, 1, attachment.Id);
        }
    }

    private async Task PublishAttachmentBindingAsync()
    {
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = _tableName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.LayerId;
    }

    private static MultipartFormDataContent AttachmentUploadForm(byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new MultipartFormDataContent { { file, "attachment", "bound.pdf" } };
    }

    private async Task<JsonDocument> ReadAttachmentJsonAsync(string url, bool allowError = false)
    {
        using var response = await _client.GetAsync(url);
        return await ParseAttachmentJsonAsync(response, allowError);
    }

    private async Task<JsonDocument> SendAttachmentJsonAsync(string url, HttpContent content, bool allowError = false)
    {
        using var response = await _client.PostAsync(url, content);
        return await ParseAttachmentJsonAsync(response, allowError);
    }

    private static async Task<JsonDocument> ParseAttachmentJsonAsync(HttpResponseMessage response, bool allowError)
    {
        var payload = await response.Content.ReadAsStringAsync();
        if (!allowError)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        }
        var document = JsonDocument.Parse(payload);
        if (!allowError)
        {
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse(payload);
        }
        return document;
    }
}
