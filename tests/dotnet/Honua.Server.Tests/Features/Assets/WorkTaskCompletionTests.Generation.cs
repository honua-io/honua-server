// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ConfigurationChangesAfterAuthorization_RejectsWithoutWrites(bool concurrentWriter)
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadMappedFeatureAsync(assetId);
        var generation = (long)(await AssetTestData.ScalarAsync(_fixture,
            "SELECT generation FROM honua.configuration_generation WHERE singleton"))!;
        var request = Completion(3, Later, 3);
        var completion = GenerationCommand(taskId, request, (generation, 2));
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskCompletionStore>();
        await using var writer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var transaction = await writer.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE honua.configuration_generation SET generation = generation + 1 WHERE singleton", writer, transaction);
        await update.ExecuteNonQueryAsync();
        if (!concurrentWriter)
        {
            await transaction.CommitAsync();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = store.CompleteAsync(completion, timeout.Token);
        if (concurrentWriter)
        {
            // Observe the actual database wait before committing; timing alone cannot prove the race.
            await WaitForDatabaseBlockAsync(writer.ProcessID, timeout.Token);
            await transaction.CommitAsync();
        }
        var result = await pending;

        result.Status.Should().Be(AssetCommandStatus.Rejected);
        result.Field.Should().Be("configurationGeneration");
        await AssertMappedRollbackAsync(taskId, (assetId, before), request.CommandId!.Value);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ConfigurationWriterWaitsUntilCompletionCommits()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var generation = (long)(await AssetTestData.ScalarAsync(_fixture,
            "SELECT generation FROM honua.configuration_generation WHERE singleton"))!;
        var request = Completion(3, Later, 3);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkTaskCompletionStore>();
        await using var taskLocker = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var taskTransaction = await taskLocker.BeginTransactionAsync();
        await using var taskLock = new NpgsqlCommand(
            "SELECT task_id FROM honua.work_tasks WHERE task_id = @id FOR UPDATE", taskLocker, taskTransaction);
        taskLock.Parameters.AddWithValue("id", taskId);
        await taskLock.ExecuteScalarAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completion = store.CompleteAsync(GenerationCommand(taskId, request, (generation, 1)), timeout.Token);
        var completionPid = await WaitForDatabaseBlockAsync(taskLocker.ProcessID, timeout.Token);

        await using var writer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var update = new NpgsqlCommand(
            "UPDATE honua.configuration_generation SET generation = generation + 1 WHERE singleton", writer);
        var changing = update.ExecuteNonQueryAsync(timeout.Token);
        await WaitForDatabaseBlockAsync(completionPid, timeout.Token);
        changing.IsCompleted.Should().BeFalse("the generation lock must survive the task wait and every completion write");
        await taskTransaction.CommitAsync();

        (await completion).Status.Should().Be(AssetCommandStatus.Accepted);
        (await changing).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(1L);
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT status FROM honua.work_tasks WHERE task_id = '{taskId}'")).Should().Be(WorkTaskStatus.Completed);
    }

    private static WorkTaskCompletionCommand GenerationCommand(Guid taskId, WorkTaskCompletionRequest request,
        (long Generation, long ProfileVersion) configuration)
        => new(taskId, request, new WorkTaskActor("crew-b", true))
        {
            Command = new AssetCommand(request.CommandId!.Value, WebAppFixture.TestServiceId, WorkTaskAction.Complete, "crew-b", new string('a', 64)),
            ConfigurationGeneration = configuration.Generation,
            AssetWriteback = new WorkTaskAssetWriteback(configuration.ProfileVersion, WebAppFixture.TestLayerId, null),
        };

    private async Task<int> WaitForDatabaseBlockAsync(int blockerPid, CancellationToken cancellationToken)
    {
        await using var observer = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var query = new NpgsqlCommand(
            "SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @blocker = ANY(pg_blocking_pids(pid)) LIMIT 1", observer);
        query.Parameters.AddWithValue("blocker", blockerPid);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await query.ExecuteScalarAsync(cancellationToken) is int pid)
            {
                return pid;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
        throw new TimeoutException("The expected PostgreSQL lock wait was not observed.");
    }
}
