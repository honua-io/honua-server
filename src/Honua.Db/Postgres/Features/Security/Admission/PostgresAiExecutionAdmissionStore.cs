// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Security.Admission;

/// <summary>Durable multi-scope AI accounting serialized by one short deployment-row transaction.</summary>
internal sealed partial class PostgresAiExecutionAdmissionStore(IAdoNetDatabaseConnectionProvider connections, string? schemaName = null)
    : IAiExecutionAdmissionStore
{
    private readonly string _policies = SchemaSearchPath.QualifyTable("ai_execution_policies", schemaName);
    private readonly string _attempts = SchemaSearchPath.QualifyTable("ai_execution_attempts", schemaName);
    private readonly string _events = SchemaSearchPath.QualifyTable("ai_execution_control_events", schemaName);

    private readonly string _financial = SchemaSearchPath.QualifyTable("ai_financial_invocations", schemaName);

    public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken)
        => AcquireCoreAsync(context, attemptId, null, cancellationToken);

    public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken)
        => AcquireCoreAsync(context, attemptId, request, cancellationToken);

    private async Task<AiAdmissionDecision> AcquireCoreAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest? request, CancellationToken cancellationToken)
    {
        if (!context.IsValid || attemptId == Guid.Empty)
            return new("invalid_context");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await SetTimeoutsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var state = await ReadStateAsync(connection, transaction, context.DeploymentId, true, cancellationToken).ConfigureAwait(false);
            if (state is null)
                return new("uninitialized");
            if (!state.Policy.IsValid)
                return new("invalid_policy");
            if (state.Stopped)
                return new("stopped");
            var now = await ReadTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (now < state.NotBeforeUtc)
                return new("recovering");

            // Keep every attempt that could enter a future supported rolling window, and every
            // live reservation. Capacity exhaustion denies; no live history is evicted to admit.
            await using (var prune = new NpgsqlCommand($"""
                DELETE FROM {_attempts} WHERE deployment_id = @deployment
                    AND admitted_at <= @cutoff AND (released_at IS NOT NULL OR lease_expires_at <= @now)
                """, connection, transaction))
            {
                prune.Parameters.AddWithValue("deployment", context.DeploymentId);
                prune.Parameters.AddWithValue("cutoff", now.AddSeconds(-AiAdmissionPolicy.MaximumWindowSeconds));
                prune.Parameters.AddWithValue("now", now);
                await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var counts = new NpgsqlCommand($"""
                SELECT COUNT(*), COALESCE(bool_or(attempt_id = @attempt), false),
                    COUNT(*) FILTER (WHERE admitted_at > @cutoff),
                    COUNT(*) FILTER (WHERE admitted_at > @cutoff AND actor_key = @actor),
                    COUNT(*) FILTER (WHERE admitted_at > @cutoff AND actor_key = @actor AND use_case = @use_case),
                    COUNT(*) FILTER (WHERE admitted_at > @cutoff AND workflow_key = @workflow),
                    COUNT(*) FILTER (WHERE released_at IS NULL AND lease_expires_at > @now),
                    COUNT(*) FILTER (WHERE released_at IS NULL AND lease_expires_at > @now AND actor_key = @actor),
                    COUNT(*) FILTER (WHERE released_at IS NULL AND lease_expires_at > @now AND actor_key = @actor AND use_case = @use_case),
                    COUNT(*) FILTER (WHERE released_at IS NULL AND lease_expires_at > @now AND workflow_key = @workflow)
                FROM {_attempts} WHERE deployment_id = @deployment
                """, connection, transaction);
            counts.Parameters.AddWithValue("deployment", context.DeploymentId);
            counts.Parameters.AddWithValue("attempt", attemptId);
            counts.Parameters.AddWithValue("actor", context.ActorKey);
            counts.Parameters.AddWithValue("use_case", context.UseCase);
            counts.Parameters.AddWithValue("workflow", NpgsqlDbType.Text, (object?)context.WorkflowKey ?? DBNull.Value);
            counts.Parameters.AddWithValue("now", now);
            counts.Parameters.AddWithValue("cutoff", now.AddSeconds(-state.Policy.WindowSeconds));
            var values = new long[8];
            long total;
            bool duplicate;
            await using (var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                total = reader.GetInt64(0);
                duplicate = reader.GetBoolean(1);
                for (var index = 0; index < values.Length; index++)
                    values[index] = reader.GetInt64(index + 2);
            }
            if (duplicate)
                return new("duplicate_attempt");
            if (total >= AiAdmissionPolicy.MaximumLedgerEntries)
                return new("ledger_full");
            var policy = state.Policy;
            var attemptLimits = new[] { policy.DeploymentAttempts, policy.ActorAttempts, policy.CohortAttempts, policy.WorkflowAttempts };
            var concurrencyLimits = new[] { policy.DeploymentConcurrency, policy.ActorConcurrency, policy.CohortConcurrency, policy.WorkflowConcurrency };
            var scopeCount = context.WorkflowKey is null ? 3 : 4;
            for (var index = 0; index < scopeCount; index++)
            {
                if (values[index] >= attemptLimits[index])
                    return new("budget_exceeded", RetryAfterSeconds: policy.WindowSeconds);
                if (values[index + 4] >= concurrencyLimits[index])
                    return new("concurrency_exceeded");
            }

            var deadline = now.AddSeconds(policy.MaxOperationSeconds);
            var expires = deadline.AddSeconds(AiAdmissionPolicy.DrainSeconds);
            var financial = await ReserveFinancialAsync(connection, transaction, state, context, attemptId, request, now, deadline, expires, cancellationToken).ConfigureAwait(false);
            if (financial.Code is not null) return new(financial.Code);
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {_attempts}
                    (deployment_id, attempt_id, generation_id, actor_key, use_case, workflow_key,
                     admitted_at, deadline_at, lease_expires_at, grant_epoch)
                VALUES (@deployment, @attempt, @generation, @actor, @use_case, @workflow, @now, @deadline, @expires, @epoch)
                """, connection, transaction);
            insert.Parameters.AddWithValue("deployment", context.DeploymentId);
            insert.Parameters.AddWithValue("attempt", attemptId);
            insert.Parameters.AddWithValue("generation", state.Generation);
            insert.Parameters.AddWithValue("actor", context.ActorKey);
            insert.Parameters.AddWithValue("use_case", context.UseCase);
            insert.Parameters.AddWithValue("workflow", NpgsqlDbType.Text, (object?)context.WorkflowKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("deadline", deadline);
            insert.Parameters.AddWithValue("expires", expires);
            insert.Parameters.AddWithValue("epoch", state.Epoch);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return new("admitted", new(context.DeploymentId, attemptId, state.Generation, state.Epoch, deadline, expires, financial.Financial));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
        {
            return new("invalid_policy");
        }
        catch (Exception exception) when (exception is DbException or TimeoutException)
        {
            // An ambiguous commit may already have charged the attempt. Never retry or mint
            // another attempt here; fail closed and let its conservative reservation expire.
            return new("unavailable");
        }
    }

    public async Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadStateAsync(connection, null, reservation.DeploymentId, false, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Generation != reservation.Generation || state.Epoch != reservation.Epoch || state.Stopped)
                return false;
            if (state.Policy.Financial is { } policy)
            {
                if (reservation.Financial is not { } financial || financial.BasisDigest != policy.Basis.Digest || !policy.IsValid) return false;
                await using var validity = new NpgsqlCommand("SELECT clock_timestamp() >= @effective AND clock_timestamp() < @expiry", connection);
                validity.Parameters.AddWithValue("effective", policy.Basis.EffectiveUtc);
                validity.Parameters.AddWithValue("expiry", policy.Basis.ExpiresUtc);
                if (await validity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) return false;
            }
            await using var command = new NpgsqlCommand($"""
                SELECT EXISTS (SELECT 1 FROM {_policies} p JOIN {_attempts} a USING (deployment_id)
                    WHERE p.deployment_id = @deployment AND p.schema_version = 1
                    AND p.generation_id = @generation AND p.epoch = @epoch AND NOT p.stopped
                    AND p.not_before <= clock_timestamp() AND a.attempt_id = @attempt
                    AND a.generation_id = @generation AND a.grant_epoch = @epoch
                    AND a.released_at IS NULL AND a.deadline_at > clock_timestamp()
                    AND a.deadline_at = @deadline AND a.lease_expires_at = @expires)
                """, connection);
            BindReservation(command, reservation);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (Exception exception) when (exception is DbException or TimeoutException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_attempts} SET released_at = COALESCE(released_at, clock_timestamp())
            WHERE deployment_id = @deployment AND attempt_id = @attempt AND generation_id = @generation AND grant_epoch = @epoch
                AND deadline_at = @deadline AND lease_expires_at = @expires
            """, connection);
        BindReservation(command, reservation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(connection, null, deploymentId, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken)
    {
        if (!command.IsValid)
            return new("invalid_control", null);
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetTimeoutsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        // The policy row does not exist during first initialization. This narrow advisory
        // lock serializes competing initializers and duplicate control invocations too.
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@deployment, 6587))", connection, transaction))
        {
            guard.Parameters.AddWithValue("deployment", command.DeploymentId);
            await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var commandHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(command, AiAdmissionStoreJsonContext.Default.AiAdmissionControlCommand))));
        await using (var replay = new NpgsqlCommand($"SELECT request_hash, result_json FROM {_events} WHERE deployment_id = @deployment AND invocation_id = @invocation", connection, transaction))
        {
            replay.Parameters.AddWithValue("deployment", command.DeploymentId);
            replay.Parameters.AddWithValue("invocation", command.InvocationId);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), commandHash, StringComparison.Ordinal))
                    return new("invalid_control", null);
                return JsonSerializer.Deserialize(reader.GetString(1), AiAdmissionStoreJsonContext.Default.AiAdmissionControlResult)
                    ?? throw new InvalidOperationException("AI admission control receipt is invalid.");
            }
        }
        var state = await ReadStateAsync(connection, transaction, command.DeploymentId, true, cancellationToken).ConfigureAwait(false);
        var now = await ReadTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        string code;
        if (command.Action == AiAdmissionControlAction.Initialize)
        {
            if (state is not null)
                return new("already_initialized", state);
            state = new(command.DeploymentId, command.Policy!, Guid.NewGuid(), 1, 1, true, now);
            code = "initialized";
        }
        else
        {
            if (state is null)
                return new("uninitialized", null);
            if (state.Generation != command.ExpectedGeneration || state.Revision != command.ExpectedRevision || state.Epoch != command.ExpectedEpoch)
                return new("stale_control", state);
            if (command.Action == AiAdmissionControlAction.UpdatePolicy)
            {
                if (!state.Stopped || await HasActiveAsync(connection, transaction, command.DeploymentId, now, cancellationToken).ConfigureAwait(false))
                    return new("policy_not_drained", state);
                if (state.Policy.Financial is { } prior && (command.Policy!.Financial is not { } next || !prior.HasSameAccountingIdentity(next)))
                    return new("invalid_control", state);
                state = state with { Policy = command.Policy!, Revision = checked(state.Revision + 1), Epoch = checked(state.Epoch + 1) };
                code = "updated";
            }
            else if (command.Action == AiAdmissionControlAction.Stop)
            {
                if (state.Stopped)
                    return new("stopped", state);
                state = state with { Stopped = true, Epoch = checked(state.Epoch + 1) };
                code = "stopped";
            }
            else
            {
                if (!state.Stopped)
                    return new("resumed", state);
                if (state.Policy.Financial is not null && await HasFinancialStopAsync(connection, transaction, command.DeploymentId, cancellationToken).ConfigureAwait(false))
                    return new("financial_stop", state);
                if (now < state.NotBeforeUtc)
                    return new("recovering", state);
                state = state with { Stopped = false, Epoch = checked(state.Epoch + 1) };
                code = "resumed";
            }
        }
        var result = new AiAdmissionControlResult(code, state, true);
        // Existing controls must use UPDATE: BEFORE INSERT runs before ON CONFLICT,
        // and must not mistake Resume/Stop for a new financial activation.
        var persistenceSql = command.Action == AiAdmissionControlAction.Initialize
            ? $"""
                INSERT INTO {_policies} (deployment_id, schema_version, policy_json, generation_id, revision, epoch, stopped, not_before)
                VALUES (@deployment, 1, @policy, @generation, @revision, @epoch, @stopped, @not_before)
                """
            : $"""
                UPDATE {_policies} SET policy_json = @policy, revision = @revision, epoch = @epoch,
                    stopped = @stopped, not_before = @not_before WHERE deployment_id = @deployment AND generation_id = @generation
                """;
        await using (var persist = new NpgsqlCommand(persistenceSql, connection, transaction))
        {
            persist.Parameters.AddWithValue("deployment", state.DeploymentId);
            persist.Parameters.AddWithValue("policy", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(state.Policy, AiAdmissionStoreJsonContext.Default.AiAdmissionPolicy));
            persist.Parameters.AddWithValue("generation", state.Generation);
            persist.Parameters.AddWithValue("revision", state.Revision);
            persist.Parameters.AddWithValue("epoch", state.Epoch);
            persist.Parameters.AddWithValue("stopped", state.Stopped);
            persist.Parameters.AddWithValue("not_before", state.NotBeforeUtc);
            await persist.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var audit = new NpgsqlCommand($"""
            INSERT INTO {_events} (deployment_id, invocation_id, request_hash, action, actor_id, correlation_id, recorded_at, result_json)
            VALUES (@deployment, @invocation, @request_hash, @action, @actor, @correlation, @now, @result)
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("deployment", command.DeploymentId);
            audit.Parameters.AddWithValue("invocation", command.InvocationId);
            audit.Parameters.AddWithValue("request_hash", commandHash);
            audit.Parameters.AddWithValue("action", command.Action.ToString());
            audit.Parameters.AddWithValue("actor", command.ActorId);
            audit.Parameters.AddWithValue("correlation", command.CorrelationId);
            audit.Parameters.AddWithValue("now", now);
            audit.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, AiAdmissionStoreJsonContext.Default.AiAdmissionControlResult));
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<AiAdmissionState?> ReadStateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string deploymentId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT schema_version, policy_json, generation_id, revision, epoch, stopped, not_before
            FROM {_policies} WHERE deployment_id = @deployment
            """ + (forUpdate ? " FOR UPDATE" : string.Empty), connection, transaction);
        command.Parameters.AddWithValue("deployment", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt32(0) != 1)
            throw new InvalidOperationException("Unsupported AI admission policy schema.");
        var policy = JsonSerializer.Deserialize(reader.GetString(1), AiAdmissionStoreJsonContext.Default.AiAdmissionPolicy)
            ?? throw new InvalidOperationException("Invalid AI admission policy.");
        var generation = reader.GetGuid(2);
        var revision = reader.GetInt64(3);
        var epoch = reader.GetInt64(4);
        if (!policy.IsValid || generation == Guid.Empty || revision <= 0 || epoch <= 0)
            throw new InvalidOperationException("Invalid AI admission control state.");
        return new(deploymentId, policy, generation, revision, epoch,
            reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    private async Task<bool> HasActiveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string deploymentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {_attempts} WHERE deployment_id = @deployment AND released_at IS NULL AND lease_expires_at > @now)", connection, transaction);
        command.Parameters.AddWithValue("deployment", deploymentId);
        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<DateTimeOffset> ReadTimeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    private static async Task SetTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindReservation(NpgsqlCommand command, AiAdmissionReservation reservation)
    {
        command.Parameters.AddWithValue("deployment", reservation.DeploymentId);
        command.Parameters.AddWithValue("attempt", reservation.AttemptId);
        command.Parameters.AddWithValue("generation", reservation.Generation);
        command.Parameters.AddWithValue("epoch", reservation.Epoch);
        command.Parameters.AddWithValue("deadline", reservation.DeadlineUtc);
        command.Parameters.AddWithValue("expires", reservation.LeaseExpiresUtc);
    }
}

[JsonSerializable(typeof(AiFinancialPriceBasis))]
[JsonSerializable(typeof(AiAdmissionPolicy))]
[JsonSerializable(typeof(AiAdmissionControlResult))]
[JsonSerializable(typeof(AiAdmissionControlCommand))]
internal sealed partial class AiAdmissionStoreJsonContext : JsonSerializerContext;
