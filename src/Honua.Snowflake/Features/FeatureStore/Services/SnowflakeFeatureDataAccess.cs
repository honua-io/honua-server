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

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Executes Snowflake feature queries built by <see cref="SnowflakeFeatureQueryBuilder"/>.
/// All commands are parameterized (positional <c>?</c> binds) and double-quoted by the builder;
/// this layer opens a connection, materializes rows, and emits telemetry.
/// </summary>
internal sealed class SnowflakeFeatureDataAccess
{
    private static readonly ActivitySource _activitySource = new("Honua.Snowflake.FeatureStore");

    private readonly ISnowflakeConnectionFactory _connectionFactory;
    private readonly IOptions<SnowflakeOptions> _options;
    private readonly ILogger<SnowflakeFeatureDataAccess> _logger;

    public SnowflakeFeatureDataAccess(
        ISnowflakeConnectionFactory connectionFactory,
        IOptions<SnowflakeOptions> options,
        ILogger<SnowflakeFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        IReadOnlyList<string> attributeColumns,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("snowflake.feature.select");
        activity?.SetTag("db.system", "snowflake");
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
                byte[]? wkb = reader.IsDBNull(1) ? null : ReadWkb(reader, 1);

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
            SnowflakeFeatureLog.QueryFailed(_logger, "select", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "select", ex);
        }
    }

    public async Task<long> ExecuteCountAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("snowflake.feature.count");
        activity?.SetTag("db.system", "snowflake");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            if (result is null or DBNull)
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
            SnowflakeFeatureLog.QueryFailed(_logger, "count", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "count", ex);
        }
    }

    public async Task<FeatureExtent?> ExecuteExtentAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("snowflake.feature.extent");
        activity?.SetTag("db.system", "snowflake");
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
            SnowflakeFeatureLog.QueryFailed(_logger, "extent", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "extent", ex);
        }
    }

    public async Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("snowflake.feature.objectids");
        activity?.SetTag("db.system", "snowflake");
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
            SnowflakeFeatureLog.QueryFailed(_logger, "objectids", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "objectids", ex);
        }
    }

    // ST_ASWKB returns BINARY. The Snowflake ADO.NET connector surfaces BINARY columns either as a
    // byte[] or as a hex string depending on session settings; accept both so the geometry decodes
    // consistently regardless of the BINARY_OUTPUT format.
    private static byte[] ReadWkb(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => bytes,
            string hex => Convert.FromHexString(hex),
            _ => (byte[])value
        };
    }

    private static Exception WrapException(int layerId, string operationType, Exception ex)
        => ex switch
        {
            TimeoutException => new TimeoutException(
                $"Snowflake {operationType} query timed out for layer {layerId}.", ex),
            _ => new InvalidOperationException(
                $"Snowflake {operationType} query failed for layer {layerId}.", ex)
        };

    private DbCommand CreateCommand(DbConnection connection, ParameterizedQuery query)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.CommandTimeout = _options.Value.CommandTimeoutSeconds;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var parameter = command.CreateParameter();
            // Snowflake.Data binds positional parameters by 1-based ordinal name.
            parameter.ParameterName = (i + 1).ToString(CultureInfo.InvariantCulture);
            parameter.Value = query.WhereParameters[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        SnowflakeFeatureLog.QueryPrepared(_logger, query.WhereParameters.Count);
        return command;
    }
}
