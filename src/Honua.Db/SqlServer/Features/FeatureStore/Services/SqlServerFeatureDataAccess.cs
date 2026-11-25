// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Security.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Db.SqlServer.Features.FeatureStore.Services;

internal sealed class SqlServerFeatureDataAccess
{
    private static readonly ActivitySource _activitySource = new("Honua.SqlServer.FeatureStore");

    private readonly ISqlServerConnectionFactory _connectionFactory;
    private readonly IOptions<SqlServerOptions> _options;
    private readonly ILogger<SqlServerFeatureDataAccess> _logger;

    public SqlServerFeatureDataAccess(
        ISqlServerConnectionFactory connectionFactory,
        IOptions<SqlServerOptions> options,
        ILogger<SqlServerFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        IReadOnlyList<string> attributeColumns,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("sqlserver.feature.select");
        activity?.SetTag("layer.id", mapping.LayerId);

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, query);

            var features = ImmutableArray.CreateBuilder<Feature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    // objectid is a stable, non-null key contract. A NULL key would silently collapse
                    // distinct rows, so fail loud (mirrors MySQL/Postgres which read it with GetInt64).
                    throw new InvalidOperationException(
                        $"SQL Server select for layer {mapping.LayerId} returned a NULL object id (column '{mapping.PrimaryKeyColumn}'); object id must be a non-null key.");
                }

                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                byte[]? wkb = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

                var attrs = ImmutableDictionary<string, object?>.Empty.ToBuilder();
                for (var i = 2; i < reader.FieldCount; i++)
                {
                    // Map each attribute by its ACTUAL reader column name. The query builder may emit
                    // only the OutFields subset, so positional indexing against the full attributeColumns
                    // list would mislabel values (mirrors MySqlFeatureDataAccess.ReadAttributes).
                    var name = reader.GetName(i);
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
            SqlServerFeatureLog.QueryFailed(_logger, "select", mapping.LayerId, ex);
            throw WrapException(mapping.LayerId, "select", ex);
        }
    }

    public Task<long> ExecuteCountAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SqlServerOperationContext { Operation = "count", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadCountAsync,
            cancellationToken);

    public Task<FeatureExtent?> ExecuteExtentAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SqlServerOperationContext { Operation = "extent", Mapping = mapping, Query = query, DataConnection = dataConnection },
            (cmd, ct) => RelationalFeatureStoreHelpers.ReadExtentAsync(cmd, mapping.Srid ?? 0, ct),
            cancellationToken);

    public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            new SqlServerOperationContext { Operation = "objectids", Mapping = mapping, Query = query, DataConnection = dataConnection },
            RelationalFeatureStoreHelpers.ReadObjectIdsAsync,
            cancellationToken);

    private sealed class SqlServerOperationContext
    {
        public required string Operation { get; init; }
        public required SqlServerLayerMapping Mapping { get; init; }
        public required ParameterizedQuery Query { get; init; }
        public DataConnection? DataConnection { get; init; }
    }

    private Task<TResult> ExecuteOperationAsync<TResult>(
        SqlServerOperationContext context,
        Func<SqlCommand, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
        => RelationalFeatureStoreHelpers.ExecuteQueryOperationAsync(
            new QueryOperationRequest<SqlConnection, SqlCommand, TResult>
            {
                ActivitySource = _activitySource,
                ActivityName = $"sqlserver.feature.{context.Operation}",
                LayerId = context.Mapping.LayerId,
                OpenConnection = ct => _connectionFactory.OpenAsync(context.DataConnection, ct),
                CreateCommand = conn => CreateCommand(conn, context.Query),
                Execute = execute,
                LogFailure = ex => SqlServerFeatureLog.QueryFailed(_logger, context.Operation, context.Mapping.LayerId, ex),
                WrapException = ex => WrapException(context.Mapping.LayerId, context.Operation, ex),
            },
            cancellationToken);

    private static Exception WrapException(int layerId, string operationType, Exception ex)
        => ex switch
        {
            TimeoutException => new TimeoutException(
                $"SQL Server {operationType} query timed out for layer {layerId}.", ex),
            _ => new InvalidOperationException(
                $"SQL Server {operationType} query failed for layer {layerId}.", ex)
        };

    private SqlCommand CreateCommand(SqlConnection connection, ParameterizedQuery query)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.CommandTimeout = _options.Value.CommandTimeoutSeconds;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var name = "@p" + i.ToString(CultureInfo.InvariantCulture);
            var value = query.WhereParameters[i] ?? DBNull.Value;
            command.Parameters.AddWithValue(name, value);
        }

        SqlServerFeatureLog.QueryPrepared(_logger, query.WhereParameters.Count);
        return command;
    }
}
