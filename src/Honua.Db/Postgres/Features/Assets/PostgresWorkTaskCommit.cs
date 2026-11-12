// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Assets;

// The shared shape of every task command: replay lookup, then history event, stored receipt and commit.
internal sealed class PostgresWorkTaskCommit
{
    public PostgresWorkTaskRows Rows { get; } = new();

    public PostgresAssetCommandReceipts Receipts { get; } = new();

    public async Task<AssetCommandResult> AcceptAsync(
        NpgsqlTransaction transaction,
        AssetCommand command,
        (WorkTaskHistoryEvent History, WorkTaskCommandResponse Response) change,
        CancellationToken cancellationToken)
    {
        var taskId = change.Response.Task.TaskId;
        await Rows.AppendHistoryAsync(transaction, taskId, change.History, cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(change.Response, AssetsJsonContext.Default.WorkTaskCommandResponse);
        var statusCode = (short)(command.Action == WorkTaskAction.Create ? 201 : 200);
        await Receipts.RecordAsync(
            transaction,
            new AssetCommandReceipt(command, AssetCommandSubject.WorkTask, taskId, statusCode, body),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new AssetCommandResult(AssetCommandStatus.Accepted, body);
    }

    public static WorkTaskCommandResponse Accepted(AssetCommand command, WorkTask task)
        => new() { Outcome = AssetCommandOutcome.Accepted, CommandId = command.CommandId, Task = task };

    public static WorkTaskHistoryEvent HistoryEvent(AssetCommand command, WorkTask? before, WorkTask after, IReadOnlyList<string> changedFields)
        => new()
        {
            EventId = Guid.NewGuid(),
            CommandId = command.CommandId,
            Action = command.Action,
            OldVersion = before?.Version,
            NewVersion = after.Version,
            OldStatus = before?.Status,
            NewStatus = after.Status,
            Actor = command.Actor,
            RecordedAt = after.UpdatedAt,
            ChangedFields = changedFields,
        };
}
