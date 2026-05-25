// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Share;

internal sealed class PostgresShareTrafficStore : IShareTrafficStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _trafficTable;

    public PostgresShareTrafficStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _trafficTable = SchemaSearchPath.QualifyTable("share_traffic_buckets", schemaName);
    }

    public Task<ShareTrafficSummary> GetSummaryAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);

            var sql = $"""
                SELECT COALESCE(SUM(public_count), 0)::bigint,
                       COALESCE(SUM(public_link_count), 0)::bigint,
                       COALESCE(SUM(embed_count), 0)::bigint,
                       COALESCE(SUM(open_data_count), 0)::bigint,
                       COALESCE(SUM(dcat_count), 0)::bigint,
                       COALESCE(SUM(stac_count), 0)::bigint,
                       COALESCE(SUM(export_count), 0)::bigint
                FROM {_trafficTable}
                WHERE bucket_start >= @period_start
                  AND bucket_start < @period_end
                  AND (@service_name IS NULL OR service_name = @service_name)
                  AND (@layer_id IS NULL OR layer_id = @layer_id)
                  AND (@resource_id IS NULL OR resource_id = @resource_id)
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            AddTrafficParameters(command, query);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var counts = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadCounts(reader, 0)
                : new ShareTrafficCounts();

            return new ShareTrafficSummary
            {
                ItemRef = query.ItemRef,
                PeriodStart = query.PeriodStart,
                PeriodEnd = query.PeriodEnd,
                ByInteractionType = counts,
                TotalRequests = Total(counts)
            };
        });

    public Task<ShareTrafficSeries> GetSeriesAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default)
        => TranslateStoreFailuresAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);

            var sql = $"""
                SELECT date_bin(@bucket_duration, bucket_start, @period_start) AS bucket_start,
                       COALESCE(SUM(public_count), 0)::bigint,
                       COALESCE(SUM(public_link_count), 0)::bigint,
                       COALESCE(SUM(embed_count), 0)::bigint,
                       COALESCE(SUM(open_data_count), 0)::bigint,
                       COALESCE(SUM(dcat_count), 0)::bigint,
                       COALESCE(SUM(stac_count), 0)::bigint,
                       COALESCE(SUM(export_count), 0)::bigint
                FROM {_trafficTable}
                WHERE bucket_start >= @period_start
                  AND bucket_start < @period_end
                  AND (@service_name IS NULL OR service_name = @service_name)
                  AND (@layer_id IS NULL OR layer_id = @layer_id)
                  AND (@resource_id IS NULL OR resource_id = @resource_id)
                GROUP BY 1
                ORDER BY 1
                """;

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection.Connection);
            AddTrafficParameters(command, query);
            command.Parameters.Add(new NpgsqlParameter("@bucket_duration", NpgsqlDbType.Interval) { Value = query.BucketDuration });

            var countsByBucket = new Dictionary<DateTimeOffset, ShareTrafficCounts>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                countsByBucket[reader.GetFieldValue<DateTimeOffset>(0)] = ReadCounts(reader, 1);
            }

            var buckets = new List<ShareTrafficBucket>();
            for (var cursor = query.PeriodStart; cursor < query.PeriodEnd; cursor = cursor.Add(query.BucketDuration))
            {
                var counts = countsByBucket.GetValueOrDefault(cursor) ?? new ShareTrafficCounts();
                buckets.Add(new ShareTrafficBucket
                {
                    BucketStart = cursor,
                    ByInteractionType = counts,
                    Total = Total(counts)
                });
            }

            return new ShareTrafficSeries
            {
                ItemRef = query.ItemRef,
                PeriodStart = query.PeriodStart,
                PeriodEnd = query.PeriodEnd,
                BucketDuration = query.BucketDuration,
                Buckets = buckets
            };
        });

    private static void AddTrafficParameters(NpgsqlCommand command, ShareTrafficQuery query)
    {
        command.Parameters.AddWithValue("@period_start", query.PeriodStart);
        command.Parameters.AddWithValue("@period_end", query.PeriodEnd);
        command.Parameters.AddWithValue("@service_name", (object?)query.ItemRef?.ServiceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@layer_id", (object?)query.ItemRef?.LayerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@resource_id", (object?)query.ItemRef?.ResourceId ?? DBNull.Value);
    }

    private static ShareTrafficCounts ReadCounts(NpgsqlDataReader reader, int offset)
        => new()
        {
            Public = reader.GetInt64(offset),
            PublicLink = reader.GetInt64(offset + 1),
            Embed = reader.GetInt64(offset + 2),
            OpenData = reader.GetInt64(offset + 3),
            Dcat = reader.GetInt64(offset + 4),
            Stac = reader.GetInt64(offset + 5),
            Export = reader.GetInt64(offset + 6)
        };

    private static long Total(ShareTrafficCounts counts)
        => counts.Public
            + counts.PublicLink
            + counts.Embed
            + counts.OpenData
            + counts.Dcat
            + counts.Stac
            + counts.Export;

    private static async Task<T> TranslateStoreFailuresAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ShareTrafficStoreUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ServiceUnavailableException ex)
        {
            throw new ShareTrafficStoreUnavailableException("Share traffic store is unavailable.", ex);
        }
        catch (NpgsqlException ex) when (IsStoreUnavailable(ex))
        {
            throw new ShareTrafficStoreUnavailableException("Share traffic store is unavailable.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new ShareTrafficStoreUnavailableException("Share traffic store is unavailable.", ex);
        }
    }

    private static bool IsStoreUnavailable(NpgsqlException exception)
    {
        if (exception is not PostgresException postgres)
        {
            return true;
        }

        return postgres.IsTransient
            || postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("53", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("57", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("28", StringComparison.Ordinal);
    }
}
