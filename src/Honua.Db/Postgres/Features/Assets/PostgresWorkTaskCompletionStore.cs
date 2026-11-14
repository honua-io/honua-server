// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.FeatureStore;
using Npgsql;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresWorkTaskCompletionStore : IWorkTaskCompletionStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly PostgresWorkTaskWriteback _writeback;
    private readonly string _generationTable;
    private readonly PostgresWorkTaskCommit _commit = new();
    private readonly PostgresWorkTaskRecords _records = new();
    private readonly PostgresWorkTaskCompletionConflicts _conflicts = new();

    public PostgresWorkTaskCompletionStore(IAdoNetDatabaseConnectionProvider connectionProvider, PostgresFeatureStoreRefactored features, string? schemaName = null)
    {
        _writeback = new PostgresWorkTaskWriteback(features);
        _generationTable = SchemaSearchPath.QualifyTable("configuration_generation", schemaName);
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<AssetCommandResult> CompleteAsync(WorkTaskCompletionCommand completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var command = completion.Command;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (await _commit.Receipts.LockAndFindAsync(transaction, command, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        // Configuration writers lock this same singleton before changing permissions or mappings.
        // Take it before task/profile locks and hold it through the entire completion commit.
        await using (var fence = new NpgsqlCommand(
            $"SELECT generation FROM {_generationTable} WHERE singleton FOR SHARE", connection, transaction))
        {
            var currentGeneration = await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentGeneration is not long value || value != completion.ConfigurationGeneration)
            {
                return new AssetCommandResult(AssetCommandStatus.Rejected, null,
                    "Configuration changed or is unavailable; reload and retry.") { Field = "configurationGeneration" };
            }
        }

        var current = await _commit.Rows.LockAsync(transaction, completion.TaskId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return AssetCommandResult.NotFound;
        }

        // Permission is settled before the current version is disclosed in a conflict.
        if (WorkTaskStateMachine.CheckPrecondition(current, WorkTaskAction.Complete, completion.Actor) is { Forbidden: true } forbidden)
        {
            return new AssetCommandResult(AssetCommandStatus.Forbidden, null, forbidden.Reason);
        }

        if (current.Status == WorkTaskStatus.Completed && completion.Request.Form == current.Form)
        {
            if (await PostgresWorkTaskAttachmentClaims.ClaimAsync(transaction, (completion, command.CommandId), cancellationToken).ConfigureAwait(false) is { } invalid)
            {
                return invalid;
            }
            return await _conflicts.RetainAsync(transaction, (current, completion), cancellationToken).ConfigureAwait(false);
        }

        if (current.Version != completion.Request.ExpectedVersion)
        {
            return AssetCommandResult.VersionConflict(command.CommandId, current.Version, "task");
        }

        if (Refuse(current, completion) is { } refused)
        {
            return refused;
        }

        return await PersistCompletionAsync(transaction, (current, completion), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssetCommandResult> PersistCompletionAsync(
        NpgsqlTransaction transaction,
        (WorkTask Current, WorkTaskCompletionCommand Completion) change,
        CancellationToken cancellationToken)
    {
        var (current, completion) = change;
        if (await PostgresWorkTaskWriteback.LockProfileAsync(transaction, change, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var command = completion.Command;
        var recordId = Guid.NewGuid();
        if (await PostgresWorkTaskAttachmentClaims.ClaimAsync(transaction, (completion, recordId), cancellationToken).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }
        var completionRecord = new WorkTaskCompletionRecord(recordId, current, completion.Request, command.Actor);
        await _records.InsertRecordAsync(transaction, completionRecord, cancellationToken).ConfigureAwait(false);
        var advanced = await _records.AdvanceSummaryAsync(transaction, completionRecord, cancellationToken).ConfigureAwait(false);
        if (advanced && await _writeback.ApplyAsync(transaction, completion.AssetWriteback, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }
        var completed = await _records.CompleteTaskAsync(transaction, completionRecord, cancellationToken).ConfigureAwait(false);

        var history = PostgresWorkTaskCommit.HistoryEvent(command, current, completed, ["status", "completion"]) with
        {
            ObservedAt = completion.Request.ObservedAt,
        };
        var inspection = current.Type == WorkTaskType.Inspection;
        var response = PostgresWorkTaskCommit.Accepted(command, completed) with
        {
            InspectionId = inspection ? recordId : null,
            ServiceRecordId = inspection ? null : recordId,
        };
        return await _commit.AcceptAsync(transaction, command, (history, response), cancellationToken).ConfigureAwait(false);
    }

    // Checked under the row lock, so the decision and the write see the same task.
    private static AssetCommandResult? Refuse(WorkTask current, WorkTaskCompletionCommand completion)
    {
        if (WorkTaskStateMachine.CheckPrecondition(current, WorkTaskAction.Complete, completion.Actor) is { } decision)
        {
            return new AssetCommandResult(decision.Forbidden ? AssetCommandStatus.Forbidden : AssetCommandStatus.Rejected, null, decision.Reason);
        }

        return completion.Request.Form == current.Form
            ? null
            : new AssetCommandResult(
                AssetCommandStatus.Rejected,
                null,
                $"The task is pinned to form {current.Form.FormId} version {current.Form.Version}.") { Field = "form" };
    }
}
