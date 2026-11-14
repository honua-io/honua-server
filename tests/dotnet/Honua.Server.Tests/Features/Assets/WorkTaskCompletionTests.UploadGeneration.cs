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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserve_ConfigurationChangesAfterAuthorization_RejectsWithoutMetadata(bool concurrentWriter)
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var generation = await AttachmentGenerationAsync();
        await using var writer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var transaction = await writer.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE honua.configuration_generation SET generation = generation + 1 WHERE singleton", writer, transaction);
        await update.ExecuteNonQueryAsync();
        if (!concurrentWriter)
        {
            await transaction.CommitAsync();
        }
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskAttachmentStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reservation = ReserveAttachmentAsync(store, (taskId, generation), timeout.Token);
        if (concurrentWriter)
        {
            await WaitForDatabaseBlockAsync(writer.ProcessID, timeout.Token);
            await transaction.CommitAsync();
        }
        (await reservation).Should().BeFalse();
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Reserve_ConfigurationWriterWaitsUntilReservationCommits()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var generation = await AttachmentGenerationAsync();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskAttachmentStore>();
        await using var taskLocker = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var transaction = await taskLocker.BeginTransactionAsync();
        await using var taskLock = new NpgsqlCommand(
            "SELECT task_id FROM honua.work_tasks WHERE task_id = @id FOR UPDATE", taskLocker, transaction);
        taskLock.Parameters.AddWithValue("id", taskId);
        await taskLock.ExecuteScalarAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reservation = ReserveAttachmentAsync(store, (taskId, generation), timeout.Token);
        var reservationPid = await WaitForDatabaseBlockAsync(taskLocker.ProcessID, timeout.Token);
        await using var writer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var update = new NpgsqlCommand(
            "UPDATE honua.configuration_generation SET generation = generation + 1 WHERE singleton", writer);
        var changing = update.ExecuteNonQueryAsync(timeout.Token);
        await WaitForDatabaseBlockAsync(reservationPid, timeout.Token);
        changing.IsCompleted.Should().BeFalse("the generation lock must protect the entire reservation transaction");
        await transaction.CommitAsync();

        (await reservation).Should().BeTrue();
        (await changing).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "work_task_attachments")).Should().Be(1);
    }

    private static Task<bool> ReserveAttachmentAsync(IWorkTaskAttachmentStore store,
        (Guid TaskId, long Generation) authorization, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        return store.ReserveAsync(new WorkTaskAttachment
        {
            AttachmentId = id, TaskId = authorization.TaskId, OwnerSubject = "crew-b", FieldId = "photo",
            Filename = "inspection.txt", ContentType = "text/plain", SizeBytes = 16,
            BlobKey = $"task-evidence/{authorization.TaskId}/{id}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }, new WorkTaskAttachmentLimits
        {
            ConfigurationGeneration = authorization.Generation, IsPlanner = false,
            MaxCount = 5, MaxFieldCount = 5, MaxTotalBytes = 1024,
        }, cancellationToken);
    }

    private async Task<long> AttachmentGenerationAsync()
        => (long)(await AssetTestData.ScalarAsync(_fixture,
            "SELECT generation FROM honua.configuration_generation WHERE singleton"))!;
}
