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

    private sealed class SnowflakeOperationContext
    {
        public required string Operation { get; init; }
        public required SnowflakeLayerMapping Mapping { get; init; }
        public required ParameterizedQuery Query { get; init; }
        public DataConnection? DataConnection { get; init; }
    }

    public SnowflakeFeatureDataAccess(
        ISnowflakeConnectionFactory connectionFactory,
        IOptions<SnowflakeOptions> options,
        ILogger<SnowflakeFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        IReadOnlyList<string> attributeColumns,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SnowflakeOperationContext { Operation = "select", Mapping = mapping, Query = query, DataConnection = dataConnection },
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadFeaturesAsync(
                cmd,
                new FeatureReadOptions
                {
                    ProviderName = "Snowflake",
                    LayerId = mapping.LayerId,
                    PrimaryKeyColumn = mapping.PrimaryKeyColumn,
                    AttributeColumns = attributeColumns,
                    ReadGeometry = ReadWkb,
                },
                ct),
            cancellationToken);

    public Task<long> ExecuteCountAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SnowflakeOperationContext { Operation = "count", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadCountAsync,
            cancellationToken);

    public Task<FeatureExtent?> ExecuteExtentAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SnowflakeOperationContext { Operation = "extent", Mapping = mapping, Query = query, DataConnection = dataConnection },
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadExtentAsync(cmd, mapping.Srid ?? 0, ct),
            cancellationToken);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SnowflakeOperationContext { Operation = "statistics", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadStatisticRowsAsync,
            cancellationToken);

    public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        SnowflakeLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SnowflakeOperationContext { Operation = "objectids", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadObjectIdsAsync,
            cancellationToken);

    private Task<TResult> ExecuteOperationAsync<TResult>(
        SnowflakeOperationContext context,
        Func<DbCommand, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            new QueryOperationRequest<DbConnection, DbCommand, TResult>
            {
                ActivitySource = _activitySource,
                ActivityName = $"snowflake.feature.{context.Operation}",
                DbSystem = "snowflake",
                LayerId = context.Mapping.LayerId,
                OpenConnection = ct => _connectionFactory.OpenAsync(context.DataConnection, ct),
                CreateCommand = conn => CreateCommand(conn, context.Query),
                Execute = execute,
                LogFailure = ex => SnowflakeFeatureLog.QueryFailed(_logger, context.Operation, context.Mapping.LayerId, ex),
                WrapException = ex => WrapException(context.Mapping.LayerId, context.Operation, ex),
            },
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
