// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    [Endpoint("GET /api/v1/tasks/{taskId}/attachments/{attachmentId}")]
    public async Task Upload_ValidFile_PersistsReadyMetadataAndDownloadsOriginalBytes()
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        using var body = UploadBody("inspection.txt", "text/plain", "Field inspection notes.");

        var response = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = Guid.Parse((await AssetTestData.ReadJsonAsync(response))["attachmentId"]!.GetValue<string>());
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{id}'")).Should().Be("ready");
        var download = await _fixture.Client.GetAsync($"{TasksPath}/{taskId}/attachments/{id}");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Be("Field inspection notes.");
        download.Headers.CacheControl!.NoStore.Should().BeTrue();
        download.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [IntegrationTheory]
    [InlineData("payload.exe", "application/octet-stream", "MZexecutable")]
    [InlineData("inspection.txt", "text/plain", "<script>alert(1)</script>")]
    [InlineData("inspection.txt", "text/plain", "")]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    public async Task Upload_UnsafeOrEmptyFile_DoesNotReserveAttachment(string filename, string contentType, string content)
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        using var body = UploadBody(filename, contentType, content);

        var response = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.DownloadAttachment)]
    [Endpoint("GET /api/v1/tasks/{taskId}/attachments/{attachmentId}")]
    public async Task Download_AttachmentBelongsToAnotherTask_ReturnsNotFound()
    {
        await UseAttachmentCompletionFormAsync();
        var first = await StartedInspectionAsync(_assets[0].AssetId);
        var second = await StartedInspectionAsync(_assets[1].AssetId);
        using var body = UploadBody("inspection.txt", "text/plain", "Field inspection notes.");
        var upload = await _fixture.Client.PostAsync($"{TasksPath}/{first}/attachments", body);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var id = (await AssetTestData.ReadJsonAsync(upload))["attachmentId"]!.GetValue<string>();

        var response = await _fixture.Client.GetAsync($"{TasksPath}/{second}/attachments/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    public async Task Upload_ConcurrentReservations_EnforcePublishedCountLimit()
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            using var body = UploadBody($"inspection-{index}.txt", "text/plain", "Field inspection notes.");
            return await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);
        }));

        responses.Count(static response => response.StatusCode == HttpStatusCode.Created).Should().Be(5);
        responses.Count(static response => response.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(5);
    }

    private async Task UseAttachmentCompletionFormAsync()
    {
        _form = await AssetTestData.PublishFormAsync(_fixture,
            [new FormFieldDefinition { FieldId = "photo", Label = "Photo", Type = "attachment", SectionId = "main" }]);
        await AssetTestData.ExecuteAsync(_fixture,
            $"UPDATE honua.asset_profiles SET inspection_form_id = '{_form.FormId}', service_form_id = '{_form.FormId}', version = version + 1 WHERE profile_id = '{_profileId}'");
    }

    private static MultipartFormDataContent UploadBody(string filename, string contentType, string content)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent("photo"), "fieldId");
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        body.Add(file, "file", filename);
        return body;
    }
}
