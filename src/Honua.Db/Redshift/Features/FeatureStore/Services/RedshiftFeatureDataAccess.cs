// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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

    private sealed class RedshiftOperationContext
    {
        public required string Operation { get; init; }
        public required RedshiftLayerMapping Mapping { get; init; }
        public required ParameterizedQuery Query { get; init; }
        public DataConnection? DataConnection { get; init; }
    }

    public RedshiftFeatureDataAccess(
        IRedshiftConnectionFactory connectionFactory,
        IOptions<RedshiftOptions> options,
        ILogger<RedshiftFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        IReadOnlyList<string> attributeColumns,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new RedshiftOperationContext { Operation = "select", Mapping = mapping, Query = query, DataConnection = dataConnection },
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadFeaturesAsync(
                cmd,
                new FeatureReadOptions
                {
                    ProviderName = "Redshift",
                    LayerId = mapping.LayerId,
                    PrimaryKeyColumn = mapping.PrimaryKeyColumn,
                    AttributeColumns = attributeColumns,
                },
                ct),
            cancellationToken);

    public Task<long> ExecuteCountAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new RedshiftOperationContext { Operation = "count", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadCountAsync,
            cancellationToken);

    public Task<FeatureExtent?> ExecuteExtentAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new RedshiftOperationContext { Operation = "extent", Mapping = mapping, Query = query, DataConnection = dataConnection },
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadExtentAsync(cmd, mapping.Srid ?? 0, ct),
            cancellationToken);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new RedshiftOperationContext { Operation = "statistics", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadStatisticRowsAsync,
            cancellationToken);

    public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        RedshiftLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new RedshiftOperationContext { Operation = "objectids", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadObjectIdsAsync,
            cancellationToken);

    private Task<TResult> ExecuteOperationAsync<TResult>(
        RedshiftOperationContext context,
        Func<NpgsqlCommand, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            new QueryOperationRequest<NpgsqlConnection, NpgsqlCommand, TResult>
            {
                ActivitySource = _activitySource,
                ActivityName = $"redshift.feature.{context.Operation}",
                LayerId = context.Mapping.LayerId,
                OpenConnection = ct => _connectionFactory.OpenAsync(context.DataConnection, ct),
                CreateCommand = conn => CreateCommand(conn, context.Query),
                Execute = execute,
                LogFailure = ex => RedshiftFeatureLog.QueryFailed(_logger, context.Operation, context.Mapping.LayerId, ex),
                WrapException = ex => WrapException(context.Mapping.LayerId, context.Operation, ex),
            },
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
