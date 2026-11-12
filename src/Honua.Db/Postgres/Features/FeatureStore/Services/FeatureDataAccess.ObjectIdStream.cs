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
    // The stopwatch covers database awaits only, so a slow client never reads as a slow query, and a
    // consumer that stops early is not recorded as a failed query.
    public async IAsyncEnumerable<long> StreamSelectObjectIdsQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var databaseTime = new Stopwatch();
        var count = 0;
        var databaseCallFailed = true;
        try
        {
            databaseTime.Start();
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                databaseTime.Stop();
                databaseCallFailed = false;
                count++;
                yield return reader.GetInt64(0);
                databaseCallFailed = true;
                databaseTime.Start();
            }

            databaseCallFailed = false;
        }
        finally
        {
            databaseTime.Stop();
            if (databaseCallFailed)
            {
                _cacheManager.RecordQueryMetrics("select_objectids_error", databaseTime.ElapsedMilliseconds);
            }
            else
            {
                RecordObjectIdQuerySuccess(layerId, databaseTime.ElapsedMilliseconds, count);
            }
        }
    }

    private void RecordObjectIdQuerySuccess(int layerId, long elapsedMilliseconds, int count)
    {
        _cacheManager.RecordQueryMetrics("select_objectids", elapsedMilliseconds, count);
        RecordPerformanceQuery("select_objectids", layerId, elapsedMilliseconds, count);
        LogSlowQuery("select_objectids", elapsedMilliseconds, layerId, count);
    }
}
