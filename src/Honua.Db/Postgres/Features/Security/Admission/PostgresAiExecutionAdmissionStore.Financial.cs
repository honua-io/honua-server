// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore
{
    private async Task<(string? Code, AiFinancialReservation? Financial)> ReserveFinancialAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AiAdmissionState state, AiExecutionContext context,
        Guid attemptId, AiFinancialRequest? request, DateTimeOffset now, DateTimeOffset deadline, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        if (state.Policy.Financial is not { } policy) return (null, null);
        if (context.UseCase != "reporting-narrative" || request is null) return ("financial_unsupported", null);
        var basis = policy.Basis;
        if (!request.Matches(basis)) return ("financial_basis_invalid", null);
        if (now < basis.EffectiveUtc || now >= basis.ExpiresUtc || now < policy.PeriodAnchorUtc) return ("financial_basis_expired", null);
        var tokens = checked(basis.MaximumInputTokens + request.MaximumOutputTokens);
        var money = basis.Charge(basis.MaximumInputTokens, request.MaximumOutputTokens);
        var periods = (now - policy.PeriodAnchorUtc).Ticks / TimeSpan.FromSeconds(policy.PeriodSeconds).Ticks;
        var period = policy.PeriodAnchorUtc.AddTicks(checked(periods * TimeSpan.FromSeconds(policy.PeriodSeconds).Ticks));
        // Policy row is already locked by the enclosing acquisition transaction. No competing
        // reserve, settlement or control can change any scope between this read and insertion.
        await using var counts = new NpgsqlCommand($"""
            SELECT COUNT(*), COALESCE(bool_or(attempt_id = @attempt), false),
                COALESCE(SUM(charged_tokens) FILTER (WHERE state <> 'settled' OR period_start = @period), 0),
                COALESCE(SUM(charged_money) FILTER (WHERE state <> 'settled' OR period_start = @period), 0),
                COALESCE(SUM(charged_tokens) FILTER (WHERE actor_key = @actor AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(SUM(charged_money) FILTER (WHERE actor_key = @actor AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(SUM(charged_tokens) FILTER (WHERE actor_key = @actor AND use_case = @use_case AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(SUM(charged_money) FILTER (WHERE actor_key = @actor AND use_case = @use_case AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(SUM(charged_tokens) FILTER (WHERE workflow_key = @workflow AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(SUM(charged_money) FILTER (WHERE workflow_key = @workflow AND (state <> 'settled' OR period_start = @period)), 0),
                COALESCE(bool_or(state IN ('overrun', 'conflict')), false)
            FROM {_financial} WHERE deployment_id = @deployment
            """, connection, transaction);
        counts.Parameters.AddWithValue("deployment", context.DeploymentId);
        counts.Parameters.AddWithValue("attempt", attemptId);
        counts.Parameters.AddWithValue("period", period);
        counts.Parameters.AddWithValue("actor", context.ActorKey);
        counts.Parameters.AddWithValue("use_case", context.UseCase);
        counts.Parameters.AddWithValue("workflow", NpgsqlDbType.Text, (object?)context.WorkflowKey ?? DBNull.Value);
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetBoolean(1)) return ("duplicate_attempt", null);
            if (reader.GetInt64(0) >= policy.JournalCapacity) return ("financial_ledger_full", null);
            if (reader.GetBoolean(10)) return ("financial_stop", null);
            AiFinancialLimits[] limits = [policy.Deployment, policy.Actor, policy.Cohort, policy.Workflow];
            for (var index = 0; index < (context.WorkflowKey is null ? 3 : 4); index++)
                if (reader.GetDecimal(2 + index * 2) + tokens > limits[index].Tokens
                    || reader.GetDecimal(3 + index * 2) + money > limits[index].Money)
                    return ("financial_budget_exceeded", null);
        }
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_financial} (deployment_id, attempt_id, generation_id, grant_epoch, deadline_at, lease_expires_at, actor_key, use_case, workflow_key,
                period_start, reserved_at, request_digest, provider, destination_digest, basis_digest, basis_json, currency, money_scale,
                reserved_input, reserved_output, reserved_money, retained_input, retained_output, charged_tokens, charged_money, state)
            VALUES (@deployment, @attempt, @generation, @epoch, @deadline, @expires, @actor, @use_case, @workflow, @period, @now,
                @request, @provider, @destination, @basis, @basis_json, @currency, @scale, @input, @output, @money, @input, @output, @tokens, @money, 'reserved')
            """, connection, transaction);
        insert.Parameters.AddWithValue("deployment", context.DeploymentId);
        insert.Parameters.AddWithValue("attempt", attemptId);
        insert.Parameters.AddWithValue("generation", state.Generation);
        insert.Parameters.AddWithValue("epoch", state.Epoch);
        insert.Parameters.AddWithValue("deadline", deadline);
        insert.Parameters.AddWithValue("expires", expires);
        insert.Parameters.AddWithValue("actor", context.ActorKey);
        insert.Parameters.AddWithValue("use_case", context.UseCase);
        insert.Parameters.AddWithValue("workflow", NpgsqlDbType.Text, (object?)context.WorkflowKey ?? DBNull.Value);
        insert.Parameters.AddWithValue("period", period);
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("request", request.RequestDigest);
        insert.Parameters.AddWithValue("provider", basis.Provider.ToLowerInvariant());
        insert.Parameters.AddWithValue("destination", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis.Destination))));
        insert.Parameters.AddWithValue("basis", basis.Digest);
        insert.Parameters.AddWithValue("basis_json", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(basis, AiAdmissionStoreJsonContext.Default.AiFinancialPriceBasis));
        insert.Parameters.AddWithValue("currency", policy.Currency);
        insert.Parameters.AddWithValue("scale", policy.MoneyScale);
        insert.Parameters.AddWithValue("input", basis.MaximumInputTokens);
        insert.Parameters.AddWithValue("output", request.MaximumOutputTokens);
        insert.Parameters.AddWithValue("money", money);
        insert.Parameters.AddWithValue("tokens", tokens);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (null, new(request.RequestDigest, basis.Digest));
    }

    public async Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.Financial is not { } financial) return false;
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetTimeoutsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, reservation.DeploymentId, true, cancellationToken).ConfigureAwait(false);
        var now = await ReadTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Stopped || state.Generation != reservation.Generation || state.Epoch != reservation.Epoch
            || state.Policy.Financial is not { IsValid: true } policy || policy.Basis.Digest != financial.BasisDigest
            || now < policy.Basis.EffectiveUtc || now >= policy.Basis.ExpiresUtc || now >= reservation.DeadlineUtc)
            return false;
        await using var commit = new NpgsqlCommand($"""
            UPDATE {_financial} f SET state = 'send-committed', send_committed_at = @now
            WHERE f.deployment_id = @deployment AND f.attempt_id = @attempt AND f.generation_id = @generation
                AND f.state = 'reserved' AND f.request_digest = @request AND f.basis_digest = @basis
                AND EXISTS (SELECT 1 FROM {_attempts} a WHERE a.deployment_id = f.deployment_id AND a.attempt_id = f.attempt_id
                    AND a.generation_id = @generation AND a.grant_epoch = @epoch AND a.released_at IS NULL
                    AND a.deadline_at = @deadline AND a.lease_expires_at = @expires)
            """, connection, transaction);
        BindReservation(commit, reservation);
        commit.Parameters.AddWithValue("now", now);
        commit.Parameters.AddWithValue("request", financial.RequestDigest);
        commit.Parameters.AddWithValue("basis", financial.BasisDigest);
        if (await commit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        // A failed or ambiguous commit never returns permission to send. The row remains
        // fully charged whether it committed or rolled back; this call is never retried.
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken)
    {
        if (reservation.Financial is not { } financial || !usage.IsValid || usage.RequestDigest != financial.RequestDigest)
            return "financial_usage_invalid";
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetTimeoutsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        // Settlement is for the original debt and basis, even after stop/epoch/revision changes.
        // This lock only serializes scope accounting; it does not require current send authority.
        var state = await ReadStateAsync(connection, transaction, reservation.DeploymentId, true, cancellationToken).ConfigureAwait(false);
        if (state is null) return "unavailable";
        AiFinancialPriceBasis basis;
        string rowState;
        string? priorDigest;
        long inputBound, outputBound, reservedMoney, chargedTokens, chargedMoney, priorInput, priorOutput;
        await using (var read = new NpgsqlCommand($"""
            SELECT basis_json, state, usage_digest, reserved_input, reserved_output, reserved_money, charged_tokens, charged_money, retained_input, retained_output
            FROM {_financial} WHERE deployment_id = @deployment AND attempt_id = @attempt AND generation_id = @generation
                AND request_digest = @request AND basis_digest = @basis FOR UPDATE
            """, connection, transaction))
        {
            BindFinancialIdentity(read, reservation);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return "financial_usage_invalid";
            basis = JsonSerializer.Deserialize(reader.GetString(0), AiAdmissionStoreJsonContext.Default.AiFinancialPriceBasis)!;
            rowState = reader.GetString(1); priorDigest = reader.IsDBNull(2) ? null : reader.GetString(2);
            inputBound = reader.GetInt64(3); outputBound = reader.GetInt64(4); reservedMoney = reader.GetInt64(5);
            chargedTokens = reader.GetInt64(6); chargedMoney = reader.GetInt64(7);
            priorInput = reader.GetInt64(8); priorOutput = reader.GetInt64(9);
        }
        if (basis is not { IsValid: true } || basis.Digest != financial.BasisDigest || usage.Model != basis.Model
            || rowState == "reserved") return "financial_usage_invalid";
        var fingerprint = string.Join('\n', usage.RequestDigest, usage.Model, usage.ProviderResponseId,
            usage.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            usage.OutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            usage.InputTokensKnown.ToString(), usage.OutputTokensKnown.ToString(),
            usage.UnrepresentableUsageDigest ?? string.Empty, usage.ContractBreachDigest ?? string.Empty, usage.UnsupportedUsageDigest ?? string.Empty);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        if (priorDigest == digest) return rowState;
        var destinationDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis.Destination)));
        await using var reuse = new NpgsqlCommand($"""
            SELECT EXISTS (SELECT 1 FROM {_financial} WHERE deployment_id = @deployment AND provider = @provider AND destination_digest = @destination
                AND provider_response_id = @response AND attempt_id <> @attempt)
            """, connection, transaction);
        reuse.Parameters.AddWithValue("provider", basis.Provider.ToLowerInvariant()); reuse.Parameters.AddWithValue("destination", destinationDigest);
        reuse.Parameters.AddWithValue("response", usage.ProviderResponseId); reuse.Parameters.AddWithValue("deployment", reservation.DeploymentId);
        reuse.Parameters.AddWithValue("attempt", reservation.AttemptId);
        var reused = await reuse.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        var fault = usage.IsUnrepresentable || usage.IsContractBreach;
        var observedTokens = checked(usage.InputTokens + usage.OutputTokens);
        var observedMoney = basis.Charge(usage.InputTokens, usage.OutputTokens);
        var retainedInput = Math.Max(priorInput, usage.InputTokens);
        var retainedOutput = Math.Max(priorOutput, usage.OutputTokens);
        var retainedTokens = checked(retainedInput + retainedOutput);
        var retainedMoney = basis.Charge(retainedInput, retainedOutput);
        var tokens = fault ? retainedTokens : observedTokens;
        var money = fault ? retainedMoney : observedMoney;
        var conflict = priorDigest is not null || reused;
        var overrun = fault || usage.InputTokens > inputBound || usage.OutputTokens > outputBound || observedMoney > reservedMoney;
        var next = conflict ? "conflict" : overrun ? "overrun" : usage.IsUnsupported ? "send-committed" : "settled";
        // Unknown dimensions cannot refund debt, nor conceal a known overrun. Retain
        // at least the reservation and every observed representable charge/counter.
        if (conflict || usage.IsUnsupported)
        {
            tokens = Math.Max(Math.Max(tokens, chargedTokens), retainedTokens);
            money = Math.Max(Math.Max(money, chargedMoney), retainedMoney);
        }
        var now = await ReadTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_financial} SET state = @state, charged_tokens = @tokens, charged_money = @money,
                retained_input = @retained_input, retained_output = @retained_output,
                usage_digest = COALESCE(usage_digest, @digest), conflict_digest = CASE WHEN @conflict THEN COALESCE(conflict_digest, @digest) ELSE conflict_digest END,
                actual_input = COALESCE(actual_input, @input), actual_output = COALESCE(actual_output, @output), settled_at = CASE WHEN @state = 'settled' THEN COALESCE(settled_at, @now) ELSE settled_at END,
                provider_response_id = CASE WHEN @reused THEN provider_response_id ELSE COALESCE(provider_response_id, @response) END,
                rejected_response_id = CASE WHEN @reused THEN COALESCE(rejected_response_id, @response) ELSE rejected_response_id END,
                unrepresentable_usage_digest = COALESCE(unrepresentable_usage_digest, @overflow),
                contract_breach_digest = COALESCE(contract_breach_digest, @breach),
                unsupported_usage_digest = COALESCE(unsupported_usage_digest, @unsupported)
            WHERE deployment_id = @deployment AND attempt_id = @attempt
            """, connection, transaction);
        update.Parameters.AddWithValue("deployment", reservation.DeploymentId); update.Parameters.AddWithValue("attempt", reservation.AttemptId);
        update.Parameters.AddWithValue("state", next); update.Parameters.AddWithValue("tokens", tokens); update.Parameters.AddWithValue("money", money);
        update.Parameters.AddWithValue("retained_input", retainedInput); update.Parameters.AddWithValue("retained_output", retainedOutput);
        update.Parameters.AddWithValue("digest", digest); update.Parameters.AddWithValue("conflict", conflict);
        update.Parameters.AddWithValue("input", NpgsqlDbType.Bigint, usage.InputTokensKnown ? usage.InputTokens : DBNull.Value);
        update.Parameters.AddWithValue("output", NpgsqlDbType.Bigint, usage.OutputTokensKnown ? usage.OutputTokens : DBNull.Value);
        update.Parameters.AddWithValue("now", now); update.Parameters.AddWithValue("response", NpgsqlDbType.Text, string.IsNullOrEmpty(usage.ProviderResponseId) ? DBNull.Value : usage.ProviderResponseId);
        update.Parameters.AddWithValue("reused", reused); update.Parameters.AddWithValue("overflow", NpgsqlDbType.Text, (object?)usage.UnrepresentableUsageDigest ?? DBNull.Value);
        update.Parameters.AddWithValue("breach", NpgsqlDbType.Text, (object?)usage.ContractBreachDigest ?? DBNull.Value);
        update.Parameters.AddWithValue("unsupported", NpgsqlDbType.Text, (object?)usage.UnsupportedUsageDigest ?? DBNull.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (next is "overrun" or "conflict")
        {
            await using var stop = new NpgsqlCommand($"UPDATE {_policies} SET stopped = TRUE, epoch = epoch + 1 WHERE deployment_id = @deployment AND stopped = FALSE", connection, transaction);
            stop.Parameters.AddWithValue("deployment", reservation.DeploymentId);
            await stop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static void BindFinancialIdentity(NpgsqlCommand command, AiAdmissionReservation reservation)
    {
        command.Parameters.AddWithValue("deployment", reservation.DeploymentId); command.Parameters.AddWithValue("attempt", reservation.AttemptId);
        command.Parameters.AddWithValue("generation", reservation.Generation); command.Parameters.AddWithValue("request", reservation.Financial!.RequestDigest);
        command.Parameters.AddWithValue("basis", reservation.Financial.BasisDigest);
    }

    private async Task<bool> HasFinancialStopAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string deployment, CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {_financial} WHERE deployment_id = @deployment AND state IN ('overrun', 'conflict'))", connection, transaction);
        query.Parameters.AddWithValue("deployment", deployment);
        return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
