// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore : IAiMonthlyActorNoticeStore
{
    private readonly string _monthlyNotices = SchemaSearchPath.QualifyTable("ai_monthly_actor_notices", schemaName);

    public async Task<IReadOnlyList<AiMonthlyActorNotice>> ListPendingAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT deployment_id, actor_key, month_start, charged_tokens, charged_money, token_limit, money_limit
            FROM {_monthlyNotices} WHERE delivered_at IS NULL
            ORDER BY created_at, deployment_id, actor_key, month_start LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var notices = new List<AiMonthlyActorNotice>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            notices.Add(new AiMonthlyActorNotice
            {
                DeploymentId = reader.GetString(0),
                ActorKey = reader.GetString(1),
                MonthStart = reader.GetFieldValue<DateTimeOffset>(2),
                ChargedTokens = reader.GetDecimal(3),
                ChargedMoney = reader.GetDecimal(4),
                TokenLimit = reader.GetInt64(5),
                MoneyLimit = reader.GetInt64(6)
            });
        }
        return notices;
    }

    public async Task<bool> MarkQueuedAsync(AiMonthlyActorNotice notice, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_monthlyNotices} SET delivered_at = clock_timestamp()
            WHERE deployment_id = @deployment AND actor_key = @actor AND month_start = @month AND delivered_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("deployment", NpgsqlDbType.Text, notice.DeploymentId);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, notice.ActorKey);
        command.Parameters.AddWithValue("month", NpgsqlDbType.TimestampTz, notice.MonthStart);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
