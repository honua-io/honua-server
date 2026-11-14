// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCrewAccessTests
{
    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/tasks/{taskId}/attachments")]
    [Endpoint("GET /api/v1/tasks/{taskId}/attachments/{attachmentId}")]
    public async Task Attachments_RejectAnonymousUnassignedAndOtherServicePrincipals()
    {
        var taskId = await StartedAttachmentTaskAsync();
        using var anonymous = _fixture.CreateClient();
        foreach (var (client, expected) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized), (_otherCrew, HttpStatusCode.Forbidden),
            (_outsider, HttpStatusCode.NotFound)
        })
        {
            using var body = AttachmentBody();
            var response = await client.PostAsync($"{TasksPath}/{taskId}/attachments", body);
            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        }
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);

        using var validBody = AttachmentBody();
        var uploaded = await _crew.PostAsync($"{TasksPath}/{taskId}/attachments", validBody);
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var attachmentId = (await AssetTestData.ReadJsonAsync(uploaded))["attachmentId"]!.GetValue<string>();
        foreach (var (client, expected) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized), (_otherCrew, HttpStatusCode.NotFound),
            (_outsider, HttpStatusCode.NotFound)
        })
        {
            var response = await client.GetAsync($"{TasksPath}/{taskId}/attachments/{attachmentId}");
            response.StatusCode.Should().Be(expected);
        }
    }

    private async Task<string> StartedAttachmentTaskAsync()
    {
        _form = await AssetTestData.PublishFormAsync(_fixture,
            [new FormFieldDefinition { FieldId = "photo", Type = "attachment", SectionId = "main", Label = "Evidence" }]);
        var reported = await PostAsync(_crew, TasksPath, Request(WorkTaskType.Request, assignee: null));
        reported.StatusCode.Should().Be(HttpStatusCode.Created, await reported.Content.ReadAsStringAsync());
        var task = (await AssetTestData.ReadJsonAsync(reported))["task"]!;
        var taskId = task["taskId"]!.GetValue<string>();
        (await PostAsync(_planner, $"{TasksPath}/{taskId}/transitions",
            Transition(WorkTaskAction.Assign, 1, task["createdBy"]!.GetValue<string>())))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(_crew, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Start, 2, null)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return taskId;
    }

    private static MultipartFormDataContent AttachmentBody()
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent("photo"), "fieldId");
        var file = new ByteArrayContent("Field inspection notes."u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "inspection.txt");
        return body;
    }
}
