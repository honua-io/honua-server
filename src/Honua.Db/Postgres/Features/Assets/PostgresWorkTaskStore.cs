// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresWorkTaskStore : IWorkTaskStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly PostgresWorkTaskCommit _commit = new();

    public PostgresWorkTaskStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<WorkTaskListResult> ListAsync(WorkTaskQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _commit.Rows.SelectCommand(
            connection,
            null,
            """
            service_id = @service_id AND object_id > @after
                AND (@status::text IS NULL OR status = @status)
                AND (@assignee::text IS NULL OR assignee = @assignee)
                AND (@task_type::text IS NULL OR task_type = @task_type)
            ORDER BY object_id LIMIT @limit
            """);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, query.ServiceId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, query.AfterObjectId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)query.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("assignee", NpgsqlDbType.Text, (object?)query.Assignee ?? DBNull.Value);
        command.Parameters.AddWithValue("task_type", NpgsqlDbType.Text, (object?)query.Type ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit + 1);

        var items = new List<WorkTask>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(PostgresWorkTaskRows.Read(reader));
            }
        }

        var hasMore = PostgresAssetReading.TrimToPage(items, query.Limit);

        return new WorkTaskListResult
        {
            Items = items,
            NextCursor = hasMore ? items[^1].ObjectId.ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    public async Task<WorkTask?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _commit.Rows.GetAsync(connection, taskId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult?> FindReceiptAsync(AssetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _commit.Receipts.FindAsync(connection, null, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult> CreateAsync(WorkTaskDraft draft, AssetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(command);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (await _commit.Receipts.LockAndFindAsync(transaction, command, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        if (!await _commit.Rows.InsertAsync(transaction, draft, cancellationToken).ConfigureAwait(false))
        {
            return new AssetCommandResult(AssetCommandStatus.VersionConflict, null, "A task with this ID already exists.");
        }

        var task = (await _commit.Rows.LockAsync(transaction, draft.Task.TaskId, cancellationToken).ConfigureAwait(false))!;
        var history = PostgresWorkTaskCommit.HistoryEvent(command, null, task, ["status", "assignee", "geometry", "form"]);
        return await _commit.AcceptAsync(transaction, command, (history, PostgresWorkTaskCommit.Accepted(command, task)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult> TransitionAsync(WorkTaskTransitionCommand transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var command = transition.Command;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (await _commit.Receipts.LockAndFindAsync(transaction, command, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        var current = await _commit.Rows.LockAsync(transaction, transition.TaskId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return AssetCommandResult.NotFound;
        }

        // Permission is settled before the current version is disclosed in a conflict.
        if (WorkTaskStateMachine.CheckPrecondition(current, transition.Request.Action ?? string.Empty, transition.Actor) is { Forbidden: true } forbidden)
        {
            return new AssetCommandResult(AssetCommandStatus.Forbidden, null, forbidden.Reason);
        }

        if (current.Version != transition.Request.ExpectedVersion)
        {
            return AssetCommandResult.VersionConflict(command.CommandId, current.Version, "task");
        }

        var decision = WorkTaskStateMachine.Decide(current, transition.Request, transition.Actor);
        if (!decision.Allowed)
        {
            return new AssetCommandResult(decision.Forbidden ? AssetCommandStatus.Forbidden : AssetCommandStatus.Rejected, null, decision.Reason);
        }

        var updated = await _commit.Rows.ApplyTransitionAsync(
            transaction,
            new WorkTaskTransitionRow(current.TaskId, current.Version, decision.NewStatus!, decision.NewAssignee, transition.Request.Reason),
            cancellationToken).ConfigureAwait(false);
        var history = PostgresWorkTaskCommit.HistoryEvent(command, current, updated, ChangedFields(current, updated)) with
        {
            ObservedAt = transition.Request.ObservedAt,
            Reason = transition.Request.Reason,
        };
        return await _commit.AcceptAsync(transaction, command, (history, PostgresWorkTaskCommit.Accepted(command, updated)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkTaskHistoryResult> ListHistoryAsync(WorkTaskHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _commit.Rows.ListHistoryAsync(connection, query, cancellationToken).ConfigureAwait(false);
    }

    private static string[] ChangedFields(WorkTask before, WorkTask after)
    {
        var changed = new List<string>();
        if (!string.Equals(before.Status, after.Status, StringComparison.Ordinal))
        {
            changed.Add("status");
        }

        if (!string.Equals(before.Assignee, after.Assignee, StringComparison.Ordinal))
        {
            changed.Add("assignee");
        }

        if (!string.Equals(before.CancelReason, after.CancelReason, StringComparison.Ordinal))
        {
            changed.Add("cancelReason");
        }

        return [.. changed];
    }
}
