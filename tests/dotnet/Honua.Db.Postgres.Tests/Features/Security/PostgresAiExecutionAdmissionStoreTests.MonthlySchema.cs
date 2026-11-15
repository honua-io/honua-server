// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed partial class PostgresAiExecutionAdmissionStoreTests
{
    [IntegrationTest]
    public async Task MonthlySchema_ConcurrentLegacyWriters_EnforceActorCapAndPersistOneNotice()
    {
        await SeedMonthlySchemaAsync(30);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await CloneMonthlyReservationAsync();
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == "23514")
            {
                return false;
            }
        }));
        results.Count(accepted => accepted).Should().Be(1);
        (await FinancialCountAsync()).Should().Be(2);
        await ApplyMonthlySchemaAsync();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".ai_monthly_actor_notices WHERE delivered_at IS NULL", connection);
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [IntegrationTest]
    public async Task MonthlySchema_UnsettledPriorMonthDebt_CannotBeResetByBackdating()
    {
        await SeedMonthlySchemaAsync(15);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET reserved_at = clock_timestamp() - INTERVAL '2 months'");
        var insert = async () => await CloneMonthlyReservationAsync();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await FinancialCountAsync()).Should().Be(1);
    }

    [IntegrationTest]
    public async Task MonthlySchema_ActivationWhileRunning_IsRejected()
    {
        await InitializeAndResumeAsync(Store(), FinancialPolicy());
        await ApplyMonthlySchemaAsync();
        var activate = async () => await SetMonthlyLimitAsync(30);
        (await activate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTest]
    public async Task MonthlySchema_ActivationWithLiveLease_IsRejected()
    {
        var store = Store(); var policy = FinancialPolicy();
        await InitializeAndResumeAsync(store, policy);
        (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Granted.Should().BeTrue();
        await ApplyMonthlySchemaAsync();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET stopped = TRUE, epoch = epoch + 1");
        var activate = async () => await SetMonthlyLimitAsync(30);
        (await activate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTest]
    public async Task MonthlySchema_RemovingConfiguredCap_IsRejected()
    {
        await SeedMonthlySchemaAsync(30);
        var remove = async () => await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET epoch = epoch + 1, policy_json = policy_json #- '{{Financial,MonthlyActor}}'");
        (await remove.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTheory]
    [InlineData("{\"Tokens\":0,\"Money\":30}")]
    [InlineData("{\"Tokens\":30,\"Money\":-1}")]
    [InlineData("{\"Tokens\":1.5,\"Money\":30}")]
    [InlineData("{\"Tokens\":\"30\",\"Money\":30}")]
    [InlineData("{\"Tokens\":30}")]
    [InlineData("{\"Tokens\":1000000000000001,\"Money\":30}")]
    public async Task MonthlySchema_InvalidLimits_AreRejected(string limits)
    {
        await SeedMonthlySchemaAsync(30);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"UPDATE \"{_schema}\".ai_execution_policies SET epoch = epoch + 1, policy_json = jsonb_set(policy_json, '{{Financial,MonthlyActor}}', @limits::jsonb)", connection);
        command.Parameters.AddWithValue("limits", limits);
        var update = async () => await command.ExecuteNonQueryAsync();
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTest]
    public async Task MonthlySchema_SettlementOverrun_PreservesDebtAndStopsNextReservation()
    {
        await SeedMonthlySchemaAsync(30);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = 45, charged_money = 45, state = 'overrun'");
        var insert = async () => await CloneMonthlyReservationAsync();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".ai_monthly_actor_notices WHERE delivered_at IS NULL", connection);
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }

    [IntegrationTest]
    public async Task MonthlySchema_ThresholdNotice_IsPersistedAtEightyPercentOnlyOnce()
    {
        await SeedMonthlySchemaAsync(30);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = 23, charged_money = 23");
        (await MonthlyNoticeCountAsync()).Should().Be(0);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = 24, charged_money = 24");
        (await MonthlyNoticeCountAsync()).Should().Be(1);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = 30, charged_money = 30");
        (await MonthlyNoticeCountAsync()).Should().Be(1);
    }

    [IntegrationTest]
    public async Task MonthlySchema_PriorMonthSettledUsage_DoesNotConsumeCurrentMonthCap()
    {
        await SeedMonthlySchemaAsync(30);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET state = 'settled', settled_at = clock_timestamp(), reserved_at = clock_timestamp() - INTERVAL '2 months'");
        await CloneMonthlyReservationAsync();
        await CloneMonthlyReservationAsync();
        var insert = async () => await CloneMonthlyReservationAsync();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await FinancialCountAsync()).Should().Be(3);
    }

    [IntegrationTest]
    public async Task MonthlySchema_ReplayingFinancialMigration_DoesNotRemoveMonthlyFence()
    {
        await SeedMonthlySchemaAsync(15);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiFinancialAccounting.sql"));
        await ExecuteAsync(sql.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
        var insert = async () => await CloneMonthlyReservationAsync();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTest]
    public async Task MonthlySchema_NonUtcSession_CountsSettledUsageThroughUtcMonthEnd()
    {
        await SeedMonthlySchemaAsync(30);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiMonthlyActorSpend.sql"));
        sql = sql.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal)
            .Replace("clock_timestamp()", "TIMESTAMPTZ '2026-03-15 12:00:00+00'", StringComparison.Ordinal);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var migration = new NpgsqlCommand(sql, connection);
        await migration.ExecuteNonQueryAsync();
        await using var usage = new NpgsqlCommand($$"""
            SET TIME ZONE 'America/New_York';
            UPDATE "{{_schema}}".ai_financial_invocations
            SET charged_tokens = 24, charged_money = 24, state = 'settled',
                settled_at = TIMESTAMPTZ '2026-03-31 23:59:00+00',
                reserved_at = TIMESTAMPTZ '2026-03-31 23:59:00+00';
            """, connection);
        await usage.ExecuteNonQueryAsync();
        (await MonthlyNoticeCountAsync()).Should().Be(1);
        await using var insert = new NpgsqlCommand($$"""
            INSERT INTO "{{_schema}}".ai_financial_invocations
            SELECT (jsonb_populate_record(NULL::"{{_schema}}".ai_financial_invocations,
                to_jsonb(f) || jsonb_build_object('attempt_id', '{{Guid.NewGuid():D}}',
                    'state', 'reserved', 'settled_at', NULL, 'charged_tokens', 7, 'charged_money', 7))).*
            FROM "{{_schema}}".ai_financial_invocations f LIMIT 1;
            """, connection);
        var reserve = async () => await insert.ExecuteNonQueryAsync();
        (await reserve.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    private async Task<long> MonthlyNoticeCountAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".ai_monthly_actor_notices WHERE delivered_at IS NULL", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SeedMonthlySchemaAsync(int limit)
    {
        var store = Store(); var policy = FinancialPolicy();
        await InitializeAndResumeAsync(store, policy);
        var reservation = await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);
        reservation.Granted.Should().BeTrue();
        await store.ReleaseAsync(reservation.Reservation!, default);
        await ApplyMonthlySchemaAsync();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET stopped = TRUE, epoch = epoch + 1");
        await SetMonthlyLimitAsync(limit);
    }

    private Task SetMonthlyLimitAsync(int limit) => ExecuteAsync($$"""
        UPDATE "{{_schema}}".ai_execution_policies
        SET epoch = epoch + 1,
            policy_json = jsonb_set(policy_json, '{Financial,MonthlyActor}', '{"Tokens":{{limit}},"Money":{{limit}}}'::jsonb)
        """);

    private async Task ApplyMonthlySchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiMonthlyActorSpend.sql"));
        await ExecuteAsync(sql.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
    }

    private Task CloneMonthlyReservationAsync() => ExecuteAsync($$"""
        INSERT INTO "{{_schema}}".ai_financial_invocations
        SELECT (jsonb_populate_record(NULL::"{{_schema}}".ai_financial_invocations,
            to_jsonb(f) || jsonb_build_object('attempt_id', '{{Guid.NewGuid():D}}',
                'reserved_at', clock_timestamp(), 'state', 'reserved', 'settled_at', NULL))).*
        FROM "{{_schema}}".ai_financial_invocations f LIMIT 1
        """);
}
