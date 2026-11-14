// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresWorkTaskCompletionConflicts
{
    private readonly PostgresAssetCommandReceipts _receipts = new();

    public async Task<AssetCommandResult> RetainAsync(
        NpgsqlTransaction transaction,
        (WorkTask Current, WorkTaskCompletionCommand Completion) change,
        CancellationToken cancellationToken)
    {
        var (current, completion) = change;
        var command = completion.Command;
        var conflict = new WorkTaskCompletionConflict
        {
            CommandId = command.CommandId,
            CandidateId = command.CommandId,
            TaskId = current.TaskId,
            CurrentVersion = current.Version,
            Candidate = completion.Request,
        };
        var body = JsonSerializer.Serialize(conflict, AssetsJsonContext.Default.WorkTaskCompletionConflict);
        var stored = await _receipts.RecordAsync(transaction,
            new AssetCommandReceipt(command, AssetCommandSubject.WorkTask, current.TaskId, 409, body),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new AssetCommandResult(AssetCommandStatus.VersionConflict, stored);
    }
}
