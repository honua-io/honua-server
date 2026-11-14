// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Core.Features.Assets;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData("pending", "crew-b", false)]
    [InlineData("ready", "another-crew", false)]
    [InlineData("ready", "crew-b", true)]
    [InlineData("expired", "crew-b", true)]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_UnownedOrUnreadyAttachment_LeavesEvidenceUnchanged(string state, string owner, bool expired)
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var attachmentId = await InsertAttachmentAsync(taskId, (state, owner, expired));
        var command = await AttachmentCommandAsync(taskId, attachmentId);

        using var scope = _fixture.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<Honua.Core.Features.Assets.IWorkTaskCompletionStore>()
            .CompleteAsync(command);

        result.Status.Should().Be(Honua.Core.Features.Assets.AssetCommandStatus.Rejected);
        result.Field.Should().Be("attachments");
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0);
        (await AssetTestData.ScalarAsync(_fixture, $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{attachmentId}'"))
            .Should().Be(state);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ReadyAttachment_ClaimsWithEvidenceAndRejectsMutation()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var attachmentId = await InsertAttachmentAsync(taskId, ("ready", "crew-b", false));
        var command = await AttachmentCommandAsync(taskId, attachmentId);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Honua.Core.Features.Assets.IWorkTaskCompletionStore>();

        (await store.CompleteAsync(command)).Status.Should().Be(Honua.Core.Features.Assets.AssetCommandStatus.Accepted);
        (await store.CompleteAsync(command)).Status.Should().Be(Honua.Core.Features.Assets.AssetCommandStatus.Duplicate);

        (await AssetTestData.ScalarAsync(_fixture, $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{attachmentId}'"))
            .Should().Be("claimed");
        (await AssetTestData.ScalarAsync(_fixture, "SELECT jsonb_array_length(attachments) FROM honua.asset_inspections")).Should().Be(1);
        var mutate = async () => await AssetTestData.ExecuteAsync(_fixture,
            $"UPDATE honua.work_task_attachments SET status = 'expired', claimed_by = NULL WHERE attachment_id = '{attachmentId}'");
        await mutate.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MixedReadyAndPendingAttachments_RollsBackPartialClaims()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var ready = await InsertAttachmentAsync(taskId, ("ready", "crew-b", false));
        var pending = await InsertAttachmentAsync(taskId, ("pending", "crew-b", false));
        var command = await AttachmentCommandAsync(taskId, ready);
        command = command with { Request = command.Request with { Attachments = [ready, pending] } };
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskCompletionStore>();

        (await store.CompleteAsync(command)).Status.Should().Be(AssetCommandStatus.Rejected);

        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT status FROM honua.work_task_attachments WHERE attachment_id = '{ready}'")).Should().Be("ready");
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task ScheduleCleanup_ExpiredUploads_RetainsTombstonesAndRetriesDeletion()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var pending = await InsertAttachmentAsync(taskId, ("pending", "crew-b", true));
        var ready = await InsertAttachmentAsync(taskId, ("ready", "crew-b", false));
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskAttachmentStore>();

        var expired = await store.ScheduleCleanupAsync();
        expired.Should().ContainSingle().Which.AttachmentId.Should().Be(pending);
        (await store.MarkReadyAsync(pending)).Should().BeFalse();
        (await store.GetAsync(ready))!.Status.Should().Be("ready");
        (await store.ScheduleCleanupAsync()).Should().BeEmpty();
        await AssetTestData.ExecuteAsync(_fixture,
            $"UPDATE honua.work_task_attachments SET cleanup_after = now() - interval '1 minute' WHERE attachment_id = '{pending}'");
        (await store.ScheduleCleanupAsync()).Should().ContainSingle().Which.AttachmentId.Should().Be(pending);
    }

    private async Task<WorkTaskCompletionCommand> AttachmentCommandAsync(Guid taskId, Guid attachmentId)
    {
        var generation = (long)(await AssetTestData.ScalarAsync(_fixture,
            "SELECT generation FROM honua.configuration_generation WHERE singleton"))!;
        return GenerationCommand(taskId, Completion(3, Later, 3) with { Attachments = [attachmentId] }, (generation, 1));
    }

    private async Task<Guid> InsertAttachmentAsync(Guid taskId, (string State, string Owner, bool Expired) upload)
    {
        var id = Guid.NewGuid();
        var expires = upload.Expired ? "now() - interval '1 minute'" : "now() + interval '1 hour'";
        await AssetTestData.ExecuteAsync(_fixture, $"""
            INSERT INTO honua.work_task_attachments
                (attachment_id, task_id, owner_subject, field_id, filename, content_type, size_bytes, blob_key, status, expires_at)
            VALUES ('{id}', '{taskId}', '{upload.Owner}', 'photo', 'inspection.jpg', 'image/jpeg', 16,
                'task-evidence/{id}', '{upload.State}', {expires});
            """);
        return id;
    }
}
