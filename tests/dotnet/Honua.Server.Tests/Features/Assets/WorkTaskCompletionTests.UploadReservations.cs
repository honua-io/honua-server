// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData("photo", 1024, ReservationOutcome.Rejected)]
    [InlineData("receipt", 32, ReservationOutcome.Accepted)]
    [InlineData("receipt", 31, ReservationOutcome.Rejected)]
    public async Task Reserve_ExistingUpload_EnforcesFieldAndTotalLimits(string field, long totalLimit, ReservationOutcome outcome)
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var generation = await AttachmentGenerationAsync();
        await InsertAttachmentAsync(taskId, ("pending", "crew-b", false));
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskAttachmentStore>();
        var id = Guid.NewGuid();

        var reserved = await store.ReserveAsync(new WorkTaskAttachment
        {
            AttachmentId = id, TaskId = taskId, OwnerSubject = "crew-b", FieldId = field,
            Filename = "inspection.txt", ContentType = "text/plain", SizeBytes = 16,
            BlobKey = $"task-evidence/{taskId}/{id}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }, new WorkTaskAttachmentLimits
        {
            ConfigurationGeneration = generation, IsPlanner = false, MaxCount = 5, MaxFieldCount = 1, MaxTotalBytes = totalLimit,
        });

        reserved.Should().Be(outcome == ReservationOutcome.Accepted);
        (await store.GetAsync(id) is not null).Should().Be(outcome == ReservationOutcome.Accepted);
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(outcome == ReservationOutcome.Accepted ? 2 : 1);
    }

    [IntegrationTheory]
    [InlineData(TaskChange.Reassigned)]
    [InlineData(TaskChange.Cancelled)]
    public async Task Reserve_TaskChangesWhileUploadWaits_RejectsWithoutMetadata(TaskChange change)
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var generation = await AttachmentGenerationAsync();
        await using var writer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var transaction = await writer.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(change == TaskChange.Cancelled
            ? "UPDATE honua.work_tasks SET status = 'cancelled', cancel_reason = 'No longer required' WHERE task_id = @id"
            : "UPDATE honua.work_tasks SET assignee = 'another-crew' WHERE task_id = @id", writer, transaction);
        update.Parameters.AddWithValue("id", taskId);
        await update.ExecuteNonQueryAsync();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskAttachmentStore>();
        var id = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reservation = store.ReserveAsync(new WorkTaskAttachment
        {
            AttachmentId = id, TaskId = taskId, OwnerSubject = "crew-b", FieldId = "photo",
            Filename = "inspection.txt", ContentType = "text/plain", SizeBytes = 16,
            BlobKey = $"task-evidence/{taskId}/{id}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }, new WorkTaskAttachmentLimits
        {
            ConfigurationGeneration = generation, IsPlanner = false, MaxCount = 5, MaxFieldCount = 5, MaxTotalBytes = 1024,
        }, timeout.Token);
        await WaitForDatabaseBlockAsync(writer.ProcessID, timeout.Token);
        await transaction.CommitAsync();

        (await reservation).Should().BeFalse();
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);
    }
}
