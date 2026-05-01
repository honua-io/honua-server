// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Executes SQL Server feature queries built by <see cref="SqlServerFeatureQueryBuilder"/>.
/// All commands are parameterized and bracket-quoted by the builder; this layer is responsible
/// for opening a pooled <see cref="SqlConnection"/>, materializing rows, and emitting telemetry.
/// </summary>
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

        return features.ToImmutable();
    }

    public async Task<long> ExecuteCountAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("sqlserver.feature.count");
        activity?.SetTag("layer.id", mapping.LayerId);

        await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, query);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
        {
            return 0L;
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<FeatureExtent?> ExecuteExtentAsync(
        SqlServerLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("sqlserver.feature.extent");
        activity?.SetTag("layer.id", mapping.LayerId);

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

        return FeatureExtent.Create(minX, minY, maxX, maxY, mapping.Srid ?? 0);
    }

    public async Task<ImmutableArray<long>> ExecuteObjectIdsAsync(
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
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

        return ids.ToImmutable();
    }

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

        SqlServerFeatureLog.QueryPrepared(_logger, query.Sql, query.WhereParameters.Count);
        return command;
    }
}
