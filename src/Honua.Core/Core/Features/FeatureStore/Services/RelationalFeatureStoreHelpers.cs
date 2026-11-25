// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

internal sealed class QueryOperationRequest<TConnection, TCommand, TResult>
    where TConnection : DbConnection
    where TCommand : DbCommand
{
    public required ActivitySource ActivitySource { get; init; }
    public required string ActivityName { get; init; }
    public string? DbSystem { get; init; }
    public required int LayerId { get; init; }
    public required Func<CancellationToken, Task<TConnection>> OpenConnection { get; init; }
    public required Func<TConnection, TCommand> CreateCommand { get; init; }
    public required Func<TCommand, CancellationToken, Task<TResult>> Execute { get; init; }
    public required Action<Exception> LogFailure { get; init; }
    public required Func<Exception, Exception> WrapException { get; init; }
}

internal sealed class FeatureReadOptions
{
    private static readonly Func<DbDataReader, int, byte[]> DefaultGeometryReader =
        static (reader, ordinal) => (byte[])reader.GetValue(ordinal);

    public required string ProviderName { get; init; }
    public required int LayerId { get; init; }
    public required string PrimaryKeyColumn { get; init; }
    public required IReadOnlyList<string> AttributeColumns { get; init; }
    public Func<DbDataReader, int, byte[]> ReadGeometry { get; init; } = DefaultGeometryReader;
}

internal sealed class StatisticsOrderByRequest
{
    public required StringBuilder Sql { get; init; }
    public required ImmutableArray<StatisticDefinition> Statistics { get; init; }
    public required IReadOnlyList<string> ResolvedGroupByColumns { get; init; }
    public required FeatureQuery Query { get; init; }
    public required Func<string, string> QuoteAttribute { get; init; }
}

internal static partial class RelationalFeatureStoreHelpers
{
    public static async Task<TResult> ExecuteQueryOperationAsync<TConnection, TCommand, TResult>(
        QueryOperationRequest<TConnection, TCommand, TResult> request,
        CancellationToken cancellationToken)
        where TConnection : DbConnection
        where TCommand : DbCommand
    {
        using var activity = StartQueryActivity(request);
        try
        {
            await using var connection = await request.OpenConnection(cancellationToken).ConfigureAwait(false);
            await using var command = request.CreateCommand(connection);
            var result = await request.Execute(command, cancellationToken).ConfigureAwait(false);
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
            request.LogFailure(ex);
            throw request.WrapException(ex);
        }
    }

    public static async Task<ImmutableArray<Feature>> ReadFeaturesAsync(
        DbCommand command,
        FeatureReadOptions options,
        CancellationToken cancellationToken)
    {
        var canonicalNames = BuildCanonicalAttributeMap(options.AttributeColumns);
        var features = ImmutableArray.CreateBuilder<Feature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                throw new InvalidOperationException(
                    $"{options.ProviderName} select for layer {options.LayerId} returned a NULL object id (column '{options.PrimaryKeyColumn}'); object id must be a non-null key.");
            }

            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            byte[]? wkb = reader.IsDBNull(1) ? null : options.ReadGeometry(reader, 1);
            var attrs = ReadCanonicalAttributes(reader, canonicalNames);
            features.Add(Feature.Create(id, wkb, attrs));
        }

        return features.ToImmutable();
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

    public static void AppendStatisticsOrderBy(StatisticsOrderByRequest request)
    {
        if (request.Query.OrderBy is not { IsDefaultOrEmpty: false } orderBy)
        {
            return;
        }

        var terms = new List<string>(orderBy.Length);
        foreach (var clause in orderBy)
        {
            var expression = ResolveStatisticsOrderBy(clause.Field, request);
            terms.Add($"{expression} {(clause.Ascending ? "ASC" : "DESC")}");
        }

        request.Sql.Append(" ORDER BY ").Append(string.Join(", ", terms));
    }

    private static Activity? StartQueryActivity<TConnection, TCommand, TResult>(
        QueryOperationRequest<TConnection, TCommand, TResult> request)
        where TConnection : DbConnection
        where TCommand : DbCommand
    {
        var activity = request.ActivitySource.StartActivity(request.ActivityName);
        if (request.DbSystem is not null)
        {
            activity?.SetTag("db.system", request.DbSystem);
        }

        activity?.SetTag("layer.id", request.LayerId);
        return activity;
    }

    private static Dictionary<string, string>? BuildCanonicalAttributeMap(IReadOnlyList<string> attributeColumns)
        => attributeColumns.Count == 0
            ? null
            : attributeColumns.ToDictionary(static c => c, static c => c, StringComparer.OrdinalIgnoreCase);

    private static ImmutableDictionary<string, object?> ReadCanonicalAttributes(
        DbDataReader reader,
        Dictionary<string, string>? canonicalNames)
    {
        var attrs = ImmutableDictionary<string, object?>.Empty.ToBuilder();
        for (var i = 2; i < reader.FieldCount; i++)
        {
            // Map each attribute by its actual reader column name (normalized to the
            // canonical schema casing when declared) because OutFields projects a subset.
            var rawName = reader.GetName(i);
            var name = canonicalNames is not null
                && canonicalNames.TryGetValue(rawName, out var canonicalName)
                ? canonicalName
                : rawName;

            attrs[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return attrs.ToImmutable();
    }

    private static string ResolveStatisticsOrderBy(string field, StatisticsOrderByRequest request)
    {
        foreach (var stat in request.Statistics)
        {
            if (stat.OutStatisticFieldName.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return request.QuoteAttribute(stat.OutStatisticFieldName);
            }
        }

        foreach (var groupField in request.ResolvedGroupByColumns)
        {
            if (groupField.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return request.QuoteAttribute(groupField);
            }
        }

        throw new ArgumentException(
            $"Invalid statistics order-by field name: {field}. Aggregate ordering must name a declared statistic alias or group-by field.");
    }
}
