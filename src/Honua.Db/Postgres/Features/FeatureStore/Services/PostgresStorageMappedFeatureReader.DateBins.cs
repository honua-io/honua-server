// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyReadSecurityAsync(query, cancellationToken).ConfigureAwait(false);
        FeatureQuerySecurity.ValidateDateBins(query, dateBin);

        var sql = new SqlBuilder();
        var timestamp = BuildEpochAwareTemporalExpression(
            ResolveColumnExpression(dateBin.BinField, sql),
            MetadataV2FieldType.DateTime);
        var boundary = DateBinBoundarySql.Build(dateBin, timestamp, origin => sql.AddParameter(origin.UtcDateTime));

        var selectParts = new List<string> { $"{boundary} AS \"boundary\"" };
        if (dateBin.OutStatistics is { IsDefaultOrEmpty: false } statistics)
        {
            AddOutStatistics(selectParts, statistics, sql);
        }
        else
        {
            selectParts.Add("COUNT(*) AS \"count\"");
        }

        sql.Append(CultureInfo.InvariantCulture, $"SELECT {string.Join(", ", selectParts)} FROM {BuildFeatureSource(query, sql)}");
        AppendFilter(sql, query);
        // Positional: a source table may itself have a "boundary" column, which GROUP BY would prefer over the alias.
        sql.Append(CultureInfo.InvariantCulture, " GROUP BY 1 ORDER BY 1");

        try
        {
            return await ReadStatisticsRowsAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UndefinedFunction &&
            dateBin.FixedInterval.HasValue &&
            exception.MessageText.Contains("date_bin", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Fixed-interval date bins need PostgreSQL 14 or later on the source database.", exception);
        }
    }
}
