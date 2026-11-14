// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Tasks;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_FirstCommitAfterReceiptLookup_ReplaysClaimedAttachment(bool changedPayload)
    {
        await UseAttachmentCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        using var body = UploadBody("inspection.txt", "text/plain", "Inspection evidence.");
        var upload = await _fixture.Client.PostAsync($"{TasksPath}/{taskId}/attachments", body);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        var attachmentId = Guid.Parse((await AssetTestData.ReadJsonAsync(upload))["attachmentId"]!.GetValue<string>());
        var request = Completion(3, Later, 3) with { Attachments = [attachmentId] };
        using var scope = _fixture.Services.CreateScope();
        var realStore = scope.ServiceProvider.GetRequiredService<IWorkTaskStore>();
        var task = (await realStore.GetAsync(taskId))!;
        var racingStore = Substitute.For<IWorkTaskStore>();
        racingStore.GetAsync(taskId, Arg.Any<CancellationToken>()).Returns(task);
        var lookups = 0;
        racingStore.FindReceiptAsync(Arg.Any<AssetCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (++lookups == 1)
            {
                // The retry has observed no receipt; the original request now commits before form validation.
                (await CompleteAsync(taskId, request)).StatusCode.Should().Be(HttpStatusCode.OK);
                return null;
            }
            return await realStore.FindReceiptAsync(call.Arg<AssetCommand>());
        });
        var retry = changedPayload ? request with { Notes = "A different completion payload." } : request;
        var context = CompletionReplayContext(scope.ServiceProvider, (task.CreatedBy, retry));
        var service = new WorkTaskCompletionService(racingStore,
            scope.ServiceProvider.GetRequiredService<IConfigurationGenerationSource>(),
            scope.ServiceProvider.GetRequiredService<WorkTaskCompletionProcessor>());

        var result = await service.CompleteAsync(context, taskId);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(
            changedPayload ? StatusCodes.Status409Conflict : StatusCodes.Status200OK);
        context.Response.Headers["Honua-Command-Outcome"].ToString().Should().Be(
            changedPayload ? string.Empty : AssetCommandOutcome.Duplicate);
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(1);
    }

    private static DefaultHttpContext CompletionReplayContext(IServiceProvider services,
        (string Actor, WorkTaskCompletionRequest Request) completion)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, completion.Actor), new Claim("roles", "admin")], "test")),
        };
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
            completion.Request, AssetsJsonContext.Default.WorkTaskCompletionRequest));
        context.Request.ContentType = "application/json";
        return context;
    }
}
