// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Npgsql;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore
{
    private sealed record MonthlyActorReservation
    {
        public required NpgsqlConnection Connection { get; init; }
        public required NpgsqlTransaction Transaction { get; init; }
        public required AiExecutionContext Context { get; init; }
        public required AiFinancialPolicy Policy { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required long Tokens { get; init; }
        public required long Money { get; init; }
    }

    private async Task<bool> ExceedsMonthlyActorAsync(MonthlyActorReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.Policy.MonthlyActor is not { } limit) return false;
        var month = new DateTimeOffset(reservation.Now.Year, reservation.Now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(SUM(charged_tokens), 0), COALESCE(SUM(charged_money), 0)
            FROM {_financial} WHERE deployment_id = @deployment AND actor_key = @actor
                AND (state <> 'settled' OR (reserved_at >= @month AND reserved_at < @next_month))
            """, reservation.Connection, reservation.Transaction);
        command.Parameters.AddWithValue("deployment", reservation.Context.DeploymentId);
        command.Parameters.AddWithValue("actor", reservation.Context.ActorKey);
        command.Parameters.AddWithValue("month", month);
        command.Parameters.AddWithValue("next_month", month.AddMonths(1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetDecimal(0) + reservation.Tokens > limit.Tokens
            || reader.GetDecimal(1) + reservation.Money > limit.Money;
    }
}
