// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresExecutionJobStore
{
    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 200;
    private const int ActiveListOverfetchFactor = 2;
    private const string InvalidCursorMessage = "Invalid job cursor.";

    public async Task<ExecutionJobPage> QueryAsync(
        ExecutionJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ExecutionJobCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!ExecutionJobCursor.TryDecode(query.Cursor, out var decoded))
            {
                throw new ArgumentException(InvalidCursorMessage, nameof(query));
            }

            cursor = decoded;
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit);
        var jobs = await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = BuildQueryCommand(connection, query, cursor, limit);
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

        if (jobs.Count <= limit)
        {
            return new ExecutionJobPage { Items = jobs };
        }

        jobs.RemoveAt(jobs.Count - 1);
        var last = jobs[^1];
        return new ExecutionJobPage
        {
            Items = jobs,
            NextCursor = new ExecutionJobCursor(last.CreatedAt, last.OperationId).Encode()
        };
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                var text = new StringBuilder(
                    $"SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs} WHERE status IN ({ExecutionJobRow.ActiveStatusList})");
                await using var command = new NpgsqlCommand { Connection = connection };
                if (kind.HasValue)
                {
                    text.Append(" AND kind = @kind");
                    command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = kind.Value.ToString() });
                }

                text.Append(" ORDER BY updated_at DESC");
                if (limit.HasValue)
                {
                    text.Append(" LIMIT @limit");
                    command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Bigint)
                    {
                        Value = (long)limit.Value * ActiveListOverfetchFactor
                    });
                }

                command.CommandText = text.ToString();
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListExpiryAndDispatchCandidatesAsync(CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs}
                    WHERE (status IN ('{nameof(ExecutionJobStatus.Provisioning)}', '{nameof(ExecutionJobStatus.Running)}')
                           AND (COALESCE(last_heartbeat_at, claimed_at) <= now() - heartbeat_timeout
                                OR claimed_at <= now() - max_duration))
                       OR (status = '{nameof(ExecutionJobStatus.Queued)}' AND queue_visible_at IS NULL
                           AND backend = '{ExecutionJobRow.LocalBackend}')
                    """,
                    connection);
                return await ReadJobsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListBackendReconciliationCandidateIdsAsync(CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                // The predicate is the one idx_execution_jobs_reconcilable is built on.
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT operation_id FROM {_jobs}
                    WHERE status IN ({ExecutionJobRow.ActiveStatusList})
                      AND NOT (status = '{nameof(ExecutionJobStatus.Queued)}' AND backend = '{ExecutionJobRow.LocalBackend}'
                               AND attempt_count = 0 AND NOT cancellation_requested)
                    """,
                    connection);
                var operationIds = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    operationIds.Add(reader.GetString(0));
                }

                return operationIds;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<int> CountAsync(AdmissionSubmissionWindow window, CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT count(*)::int FROM (
                        SELECT 1 FROM {_jobs}
                        WHERE kind = @kind AND lookup_keys @> @rate_key AND created_at > now() - @window
                        LIMIT @cap) AS charged
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = window.Kind.ToString() });
                command.Parameters.Add(new NpgsqlParameter("rate_key", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = new[] { ExecutionJobRow.LookupKey(ExecutionJobLookupDimensions.AdmissionRate, window.RatePrincipal) }
                });
                command.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = window.Window });
                command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = window.CountCap });
                return (int)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT now()", connection);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                return reader.GetFieldValue<DateTimeOffset>(0);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    private NpgsqlCommand BuildQueryCommand(
        NpgsqlConnection connection,
        ExecutionJobQuery query,
        ExecutionJobCursor? cursor,
        int limit)
    {
        var command = new NpgsqlCommand { Connection = connection };
        var predicates = new List<string>();

        AddFieldPredicates(command, predicates, query);
        AddLookupKeyPredicate(command, predicates, query);

        if (cursor is { } position)
        {
            predicates.Add("(created_at, operation_id) < (@cursor_created_at, @cursor_operation_id)");
            command.Parameters.Add(ExecutionJobRow.Timestamp("cursor_created_at", position.CreatedAt));
            command.Parameters.Add(new NpgsqlParameter("cursor_operation_id", NpgsqlDbType.Text) { Value = position.OperationId });
        }

        var text = new StringBuilder($"SELECT {ExecutionJobRow.SelectColumns} FROM {_jobs}");
        if (predicates.Count > 0)
        {
            text.Append(" WHERE ").AppendJoin(" AND ", predicates);
        }

        text.Append(" ORDER BY created_at DESC, operation_id DESC LIMIT @limit");
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        command.CommandText = text.ToString();
        return command;
    }

    private static void AddFieldPredicates(NpgsqlCommand command, List<string> predicates, ExecutionJobQuery query)
    {
        if (query.Statuses.Count > 0)
        {
            predicates.Add("status = ANY (@statuses)");
            command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = query.Statuses.Select(status => status.ToString()).Distinct().ToArray()
            });
        }

        if (query.Kind.HasValue)
        {
            predicates.Add("kind = @kind");
            command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = query.Kind.Value.ToString() });
        }

        if (query.ApplyTenantScope)
        {
            predicates.Add("tenant_id IS NOT DISTINCT FROM @tenant_id");
            command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Text) { Value = (object?)query.TenantId ?? DBNull.Value });
        }

        if (query.CreatedFrom.HasValue)
        {
            predicates.Add("created_at >= @created_from");
            command.Parameters.Add(ExecutionJobRow.Timestamp("created_from", query.CreatedFrom));
        }

        if (query.CreatedTo.HasValue)
        {
            predicates.Add("created_at < @created_to");
            command.Parameters.Add(ExecutionJobRow.Timestamp("created_to", query.CreatedTo));
        }
    }

    private static void AddLookupKeyPredicate(NpgsqlCommand command, List<string> predicates, ExecutionJobQuery query)
    {
        var requiredLookupKeys = RequiredLookupKeys(query);
        if (requiredLookupKeys.Length > 0)
        {
            predicates.Add("lookup_keys @> @required_lookup_keys");
            command.Parameters.Add(new NpgsqlParameter("required_lookup_keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = requiredLookupKeys
            });
        }
    }

    private static string[] RequiredLookupKeys(ExecutionJobQuery query)
    {
        var keys = new List<string>();
        Add(ExecutionJobLookupDimensions.Backend, query.Backend);
        Add(ExecutionJobLookupDimensions.Queue, query.Queue);
        Add(ExecutionJobLookupDimensions.RequestedBy, query.RequestedBy);
        Add(ExecutionJobLookupDimensions.Correlation, query.CorrelationId);
        Add(ExecutionJobLookupDimensions.Trace, query.TraceId);
        Add(ExecutionJobLookupDimensions.Definition, query.DefinitionId);
        Add(ExecutionJobLookupDimensions.Resource, query.ResourceRef);
        Add(ExecutionJobLookupDimensions.Environment, query.Environment);
        Add(ExecutionJobLookupDimensions.Server, query.Server);
        Add(ExecutionJobLookupDimensions.Release, query.ReleaseId);
        Add(ExecutionJobLookupDimensions.ChangeSet, query.ChangeSetId);
        Add(ExecutionJobLookupDimensions.Alert, query.AlertId);
        return [.. keys];

        void Add(string dimension, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add(ExecutionJobRow.LookupKey(dimension, value));
            }
        }
    }

    private static async Task<List<ExecutionJobRecord>> ReadJobsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var jobs = new List<ExecutionJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ExecutionJobRow.Read(reader));
        }

        return jobs;
    }
}
