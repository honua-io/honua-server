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
        (AssetCommand Command, WorkTaskHistoryEvent History, WorkTaskCommandResponse Response) change,
        CancellationToken cancellationToken)
    {
        var command = change.Command;
        var taskId = change.Response.Task.TaskId;
        await Rows.AppendHistoryAsync(transaction, (taskId, change.History), cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(change.Response, AssetsJsonContext.Default.WorkTaskCommandResponse);
        var statusCode = (short)(command.Action == WorkTaskAction.Create ? 201 : 200);
        await Receipts.RecordAsync(
            transaction,
            new AssetCommandReceipt
            {
                Command = command, SubjectKind = AssetCommandSubject.WorkTask, SubjectId = taskId,
                StatusCode = statusCode, ResponseJson = body,
            },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new AssetCommandResult(AssetCommandStatus.Accepted, body);
    }

    public static WorkTaskCommandResponse Accepted(AssetCommand command, WorkTask task)
        => new() { Outcome = AssetCommandOutcome.Accepted, CommandId = command.CommandId, Task = task };

    public static WorkTaskHistoryEvent HistoryEvent(AssetCommand command, (WorkTask? Before, WorkTask After) state, IReadOnlyList<string> changedFields)
        => new()
        {
            EventId = Guid.NewGuid(),
            CommandId = command.CommandId,
            Action = command.Action,
            OldVersion = state.Before?.Version,
            NewVersion = state.After.Version,
            OldStatus = state.Before?.Status,
            NewStatus = state.After.Status,
            Actor = command.Actor,
            RecordedAt = state.After.UpdatedAt,
            ChangedFields = changedFields,
        };
}
