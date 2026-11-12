// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async IAsyncEnumerable<long> StreamSelectObjectIdsQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        var completed = false;
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                count++;
                yield return reader.GetInt64(0);
            }

            completed = true;
        }
        finally
        {
            stopwatch.Stop();
            if (completed)
            {
                _cacheManager.RecordQueryMetrics("select_objectids", stopwatch.ElapsedMilliseconds, count);
                RecordPerformanceQuery("select_objectids", layerId, stopwatch.ElapsedMilliseconds, count);
                LogSlowQuery("select_objectids", stopwatch.ElapsedMilliseconds, layerId, count);
            }
            else
            {
                _cacheManager.RecordQueryMetrics("select_objectids_error", stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
