// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Executes Redshift feature queries built by <see cref="RedshiftFeatureQueryBuilder"/>.
/// All commands are parameterized and double-quoted by the builder; this layer opens a pooled
/// <see cref="NpgsqlConnection"/>, materializes rows, and emits telemetry.
/// </summary>
internal sealed class RedshiftFeatureDataAccess
{
    private static readonly ActivitySource _activitySource = new("Honua.Redshift.FeatureStore");

    private readonly IRedshiftConnectionFactory _connectionFactory;
    private readonly IOptions<RedshiftOptions> _options;
    private readonly ILogger<RedshiftFeatureDataAccess> _logger;

    public RedshiftFeatureDataAccess(
        IRedshiftConnectionFactory connectionFactory,
        IOptions<RedshiftOptions> options,
        ILogger<RedshiftFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        IReadOnlyList<string> attributeColumns,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("redshift.feature.select");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

            var features = ImmutableArray.CreateBuilder<Feature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                byte[]? wkb = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

                var attrs = ImmutableDictionary<string, object?>.Empty.ToBuilder();
                for (var i = 2; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (i - 2 < attributeColumns.Count)
                    {
                        name = attributeColumns[i - 2];
                    }

                    attrs[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                features.Add(Feature.Create(id, wkb, attrs.ToImmutable()));
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return features.ToImmutable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RedshiftFeatureLog.QueryFailed(_logger, "select", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "select", ex);
        }
    }

    public async Task<long> ExecuteCountAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("redshift.feature.count");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            if (result is null || result is DBNull)
            {
                return 0L;
            }

            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RedshiftFeatureLog.QueryFailed(_logger, "count", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "count", ex);
        }
    }

    public async Task<FeatureExtent?> ExecuteExtentAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("redshift.feature.extent");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

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

            activity?.SetStatus(ActivityStatusCode.Ok);
            return FeatureExtent.Create(minX, minY, maxX, maxY, mapping.Srid ?? 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RedshiftFeatureLog.QueryFailed(_logger, "extent", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "extent", ex);
        }
    }

    public async Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("redshift.feature.objectids");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

            var ids = ImmutableArray.CreateBuilder<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return ids.ToImmutable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RedshiftFeatureLog.QueryFailed(_logger, "objectids", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "objectids", ex);
        }
    }

    private static Exception WrapException(int layerId, string operationType, Exception ex)
        => ex switch
        {
            TimeoutException => new TimeoutException(
                $"Redshift {operationType} query timed out for layer {layerId}.", ex),
            _ => new InvalidOperationException(
                $"Redshift {operationType} query failed for layer {layerId}.", ex)
        };

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection, ParameterizedQuery query)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.CommandTimeout = _options.Value.CommandTimeoutSeconds;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var name = "p" + i.ToString(CultureInfo.InvariantCulture);
            var value = query.WhereParameters[i] ?? (object)DBNull.Value;
            command.Parameters.AddWithValue(name, value);
        }

        RedshiftFeatureLog.QueryPrepared(_logger, query.WhereParameters.Count);
        return command;
    }
}
