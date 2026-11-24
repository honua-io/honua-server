// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Db.Snowflake.Features.FeatureStore.Services;

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

            var canonicalAttributeNames = attributeColumns.Count == 0
                ? null
                : attributeColumns.ToDictionary(static c => c, static c => c, StringComparer.OrdinalIgnoreCase);

            var features = ImmutableArray.CreateBuilder<Feature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    throw new InvalidOperationException(
                        $"Snowflake select for layer {mapping.LayerId} returned a NULL object id (column '{mapping.PrimaryKeyColumn}'); object id must be a non-null key.");
                }

                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                byte[]? wkb = reader.IsDBNull(1) ? null : ReadWkb(reader, 1);

                var attrs = ImmutableDictionary<string, object?>.Empty.ToBuilder();
                for (var i = 2; i < reader.FieldCount; i++)
                {
                    // Map each attribute by its actual reader column name (normalized to the
                    // canonical schema casing when declared) because OutFields projects a subset.
                    var rawName = reader.GetName(i);
                    var name = canonicalAttributeNames is not null
                        && canonicalAttributeNames.TryGetValue(rawName, out var canonicalName)
                        ? canonicalName
                        : rawName;

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

    public Task<long> ExecuteCountAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "snowflake.feature.count",
            "snowflake",
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadCountAsync,
            ex => SnowflakeFeatureLog.QueryFailed(_logger, "count", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "count", ex),
            cancellationToken);

    public Task<FeatureExtent?> ExecuteExtentAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "snowflake.feature.extent",
            "snowflake",
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadExtentAsync(cmd, mapping.Srid ?? 0, ct),
            ex => SnowflakeFeatureLog.QueryFailed(_logger, "extent", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "extent", ex),
            cancellationToken);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "snowflake.feature.statistics",
            "snowflake",
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadStatisticRowsAsync,
            ex => SnowflakeFeatureLog.QueryFailed(_logger, "statistics", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "statistics", ex),
            cancellationToken);

    public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "snowflake.feature.objectids",
            "snowflake",
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadObjectIdsAsync,
            ex => SnowflakeFeatureLog.QueryFailed(_logger, "objectids", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "objectids", ex),
            cancellationToken);

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
