// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Caching;

// Edit-purge submissions for the UTC month, incremented by one statement so two tasks cannot both pass the cap.
internal sealed partial class PostgresCdnPurgeBudget(JobStoreDatabase database, ILogger<PostgresCdnPurgeBudget> logger)
    : ICdnPurgeBudget
{
    private readonly string _table = database.Table("cdn_purge_budget");

    public async Task<CdnPurgeAdmission> TryAdmitEditAsync(
        int monthlyLimit,
        decimal unitCost,
        CancellationToken cancellationToken = default)
    {
        if (monthlyLimit <= 0)
        {
            return new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, 0, 0);
        }

        var unit = unitCost < 0 ? 0 : unitCost;
        try
        {
            return await database.ExecuteAsync(
                (connection, _, token) => AdmitAsync(connection, monthlyLimit, unit, token),
                StatementRepeat.NotAfterSend,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Unreadable(logger, exception);
            return new CdnPurgeAdmission(CdnPurgeBudgetDecision.Unavailable, 0, 0);
        }
    }

    public async Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.ExecuteAsync(
                (connection, _, token) => ReadAsync(connection, token),
                StatementRepeat.SafeToRepeat,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Unreadable(logger, exception);
            return null;
        }
    }

    private async Task<CdnPurgeAdmission> AdmitAsync(
        NpgsqlConnection connection,
        int monthlyLimit,
        decimal unitCost,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} AS budget (month_utc, submitted_count, estimated_spend, exceeded_at)
            SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM'), 1, @unit,
                   CASE WHEN @limit <= 1 THEN now() ELSE NULL END
            ON CONFLICT (month_utc) DO UPDATE
            SET submitted_count = budget.submitted_count + 1,
                estimated_spend = budget.estimated_spend + EXCLUDED.estimated_spend,
                exceeded_at = CASE
                    WHEN budget.submitted_count + 1 >= @limit THEN COALESCE(budget.exceeded_at, now())
                    ELSE budget.exceeded_at
                END
            WHERE budget.submitted_count < @limit
            RETURNING submitted_count, estimated_spend
            """,
            connection)
        {
            CommandTimeout = 5,
        };
        command.Parameters.AddWithValue("limit", monthlyLimit);
        command.Parameters.AddWithValue("unit", unitCost);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new CdnPurgeAdmission(
                CdnPurgeBudgetDecision.Admitted,
                reader.GetInt32(0),
                reader.GetDecimal(1));
        }

        return new CdnPurgeAdmission(CdnPurgeBudgetDecision.Exceeded, monthlyLimit, 0);
    }

    private async Task<CdnPurgeBudgetMonth?> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT submitted_count, estimated_spend, exceeded_at IS NOT NULL
            FROM {_table}
            WHERE month_utc = to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM')
            """,
            connection)
        {
            CommandTimeout = 5,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CdnPurgeBudgetMonth(reader.GetInt32(0), reader.GetDecimal(1), reader.GetBoolean(2));
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10315,
            Level = LogLevel.Warning,
            Message = "The CDN purge budget could not be read.")]
        public static partial void Unreadable(ILogger logger, Exception exception);
    }
}
