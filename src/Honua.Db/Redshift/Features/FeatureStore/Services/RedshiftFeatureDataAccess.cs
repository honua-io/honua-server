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
using Npgsql;

namespace Honua.Db.Redshift.Features.FeatureStore.Services;

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
                        $"Redshift select for layer {mapping.LayerId} returned a NULL object id (column '{mapping.PrimaryKeyColumn}'); object id must be a non-null key.");
                }

                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                byte[]? wkb = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

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
            RedshiftFeatureLog.QueryFailed(_logger, "select", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "select", ex);
        }
    }

    public Task<long> ExecuteCountAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "redshift.feature.count",
            dbSystem: null,
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadCountAsync,
            ex => RedshiftFeatureLog.QueryFailed(_logger, "count", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "count", ex),
            cancellationToken);

    public Task<FeatureExtent?> ExecuteExtentAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "redshift.feature.extent",
            dbSystem: null,
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadExtentAsync(cmd, mapping.Srid ?? 0, ct),
            ex => RedshiftFeatureLog.QueryFailed(_logger, "extent", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "extent", ex),
            cancellationToken);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "redshift.feature.statistics",
            dbSystem: null,
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadStatisticRowsAsync,
            ex => RedshiftFeatureLog.QueryFailed(_logger, "statistics", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "statistics", ex),
            cancellationToken);

    public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            _activitySource,
            "redshift.feature.objectids",
            dbSystem: null,
            mapping.LayerId,
            ct => _connectionFactory.OpenAsync(dataConnection, ct),
            conn => CreateCommand(conn, query),
            RelationalFeatureStoreHelpers.ReadObjectIdsAsync,
            ex => RedshiftFeatureLog.QueryFailed(_logger, "objectids", mapping.LayerId, ex),
            ex => WrapException(mapping.LayerId, "objectids", ex),
            cancellationToken);

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
