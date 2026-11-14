// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

internal static class PostgresWorkTaskAttachmentClaims
{
    public static async Task<AssetCommandResult?> ClaimAsync(
        NpgsqlTransaction transaction,
        (WorkTaskCompletionCommand Completion, Guid EvidenceId) claim,
        CancellationToken cancellationToken)
    {
        var (completion, evidenceId) = claim;
        var ids = completion.Request.Attachments;
        if (ids is null || ids.Distinct().Count() != ids.Length || ids.Contains(Guid.Empty))
        {
            return Invalid();
        }
        if (ids.Length == 0)
        {
            return null;
        }

        await using var command = new NpgsqlCommand("""
            WITH locked AS MATERIALIZED (
                SELECT attachment_id FROM honua.work_task_attachments
                WHERE attachment_id = ANY(@ids) ORDER BY attachment_id FOR UPDATE
            )
            UPDATE honua.work_task_attachments AS a SET status = 'claimed', claimed_by = @evidence_id
            FROM locked WHERE a.attachment_id = locked.attachment_id
                AND a.task_id = @task_id AND a.owner_subject = @owner
                AND a.status = 'ready' AND a.expires_at > clock_timestamp()
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        command.Parameters.AddWithValue("task_id", NpgsqlDbType.Uuid, completion.TaskId);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, completion.Command.Actor);
        command.Parameters.AddWithValue("evidence_id", NpgsqlDbType.Uuid, evidenceId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == ids.Length ? null : Invalid();
    }

    private static AssetCommandResult Invalid()
        => new(AssetCommandStatus.Rejected, null, "Attachments must be ready, unexpired uploads owned by the completing actor for this task.")
        { Field = "attachments" };
}
