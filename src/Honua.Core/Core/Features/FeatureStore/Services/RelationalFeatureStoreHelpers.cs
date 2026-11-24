// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

internal static class RelationalFeatureStoreHelpers
{
    public static async Task<TResult> ExecuteQueryOperationAsync<TConnection, TCommand, TResult>(
        ActivitySource activitySource,
        string activityName,
        string? dbSystem,
        int layerId,
        Func<CancellationToken, Task<TConnection>> openConnection,
        Func<TConnection, TCommand> createCommand,
        Func<TCommand, CancellationToken, Task<TResult>> execute,
        Action<Exception> logFailure,
        Func<Exception, Exception> wrapException,
        CancellationToken cancellationToken)
        where TConnection : DbConnection
        where TCommand : DbCommand
    {
        using var activity = activitySource.StartActivity(activityName);
        if (dbSystem is not null)
        {
            activity?.SetTag("db.system", dbSystem);
        }

        activity?.SetTag("layer.id", layerId);

        try
        {
            await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
            await using var command = createCommand(connection);
            var result = await execute(command, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logFailure(ex);
            throw wrapException(ex);
        }
    }

    public static async Task<long> ReadCountAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return 0L;
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<FeatureExtent?> ReadExtentAsync(
        DbCommand command,
        int srid,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            return null;
        }

        var minX = Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
        var minY = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
        var maxX = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
        var maxY = Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture);
        return FeatureExtent.Create(minX, minY, maxX, maxY, srid);
    }

    public static async Task<ImmutableArray<long>> ReadObjectIdsAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var ids = ImmutableArray.CreateBuilder<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }

        return ids.ToImmutable();
    }

    public static async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ReadStatisticRowsAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row.ToImmutable());
        }

        return rows.ToImmutable();
    }

    public static void AppendStatisticsOrderBy(
        StringBuilder sb,
        ImmutableArray<StatisticDefinition> statistics,
        IReadOnlyList<string> resolvedGroupByColumns,
        FeatureQuery query,
        Func<string, string> quoteAttribute)
    {
        if (query.OrderBy is not { IsDefaultOrEmpty: false } orderBy)
        {
            return;
        }

        var terms = new List<string>(orderBy.Length);
        foreach (var clause in orderBy)
        {
            var expression = ResolveStatisticsOrderBy(clause.Field, statistics, resolvedGroupByColumns, quoteAttribute);
            terms.Add($"{expression} {(clause.Ascending ? "ASC" : "DESC")}");
        }

        sb.Append(" ORDER BY ").Append(string.Join(", ", terms));
    }

    private static string ResolveStatisticsOrderBy(
        string field,
        ImmutableArray<StatisticDefinition> statistics,
        IReadOnlyList<string> resolvedGroupByColumns,
        Func<string, string> quoteAttribute)
    {
        foreach (var stat in statistics)
        {
            if (stat.OutStatisticFieldName.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return quoteAttribute(stat.OutStatisticFieldName);
            }
        }

        foreach (var groupField in resolvedGroupByColumns)
        {
            if (groupField.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return quoteAttribute(groupField);
            }
        }

        throw new ArgumentException(
            $"Invalid statistics order-by field name: {field}. Aggregate ordering must name a declared statistic alias or group-by field.");
    }
}
