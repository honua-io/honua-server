// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Tasks;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    public async Task Upload_CompletedTask_DoesNotReserveAttachment()
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        (await CompleteAsync(taskId, Completion(3, Later, 3))).StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = UploadBody("inspection.txt", "text/plain", "Late evidence.");

        var upload = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);

        upload.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_UploadedAttachment_RetainsDownloadAcrossReplay()
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        using var body = UploadBody("inspection.txt", "text/plain", "Field inspection notes.");
        var upload = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var id = Guid.Parse((await AssetTestData.ReadJsonAsync(upload))["attachmentId"]!.GetValue<string>());
        var request = Completion(3, Later, 3) with { Attachments = [id] };

        (await CompleteAsync(taskId, request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CompleteAsync(taskId, request)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(1);
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{id}'")).Should().Be("claimed");
        var download = await _fixture.Client.GetAsync($"{TasksPath}/{taskId}/attachments/{id}");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Be("Field inspection notes.");
    }

    [IntegrationTest]
    public async Task Cleanup_ExpiredUpload_DeletesBlobAndPreservesMetadataTombstone()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var id = await InsertAttachmentAsync(taskId, ("pending", "crew-b", true));
        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();
        await using var bytes = new MemoryStream("expired evidence"u8.ToArray());
        var uploaded = await storage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = bytes, FileName = "inspection.jpg", ContentType = "image/jpeg",
            SizeBytes = bytes.Length, ObjectKeyOverride = $"task-evidence/{id}", EnableChunkedUpload = false,
        }, null);
        uploaded.Success.Should().BeTrue();

        var cleanup = new WorkTaskAttachmentCleanup(
            scope.ServiceProvider.GetRequiredService<Honua.Core.Features.Assets.IWorkTaskAttachmentStore>(), storage);
        (await cleanup.RunAsync(CancellationToken.None)).Should().Be(1);

        (await storage.ExistsAsync($"task-evidence/{id}")).Should().BeFalse();
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{id}'")).Should().Be("expired");
    }
}
