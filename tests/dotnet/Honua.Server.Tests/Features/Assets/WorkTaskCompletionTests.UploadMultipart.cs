// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData("duplicate-field")]
    [InlineData("extra-file")]
    [InlineData("missing-field")]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    public async Task Upload_MalformedMultipart_DoesNotReserveAttachment(string shape)
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        using var body = shape == "missing-field"
            ? new MultipartFormDataContent()
            : UploadBody("inspection.txt", "text/plain", "Inspection evidence.");
        if (shape == "duplicate-field")
        {
            body.Add(new StringContent("photo"), "fieldId");
        }
        else
        {
            var file = new StringContent("Additional evidence.");
            body.Add(file, "file", "additional.txt");
        }

        var response = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);
    }
}
