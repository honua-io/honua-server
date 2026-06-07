// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Executes Oracle feature queries built by <see cref="OracleFeatureQueryBuilder"/>. All
/// commands are parameterized and double-quoted by the builder; this layer is responsible
/// for opening a pooled <see cref="OracleConnection"/>, materializing rows, and emitting telemetry.
/// </summary>
internal sealed class OracleFeatureDataAccess
{
    private static readonly ActivitySource _activitySource = new("Honua.Oracle.FeatureStore");

    private readonly IOracleConnectionFactory _connectionFactory;
    private readonly IOptions<OracleOptions> _options;
    private readonly ILogger<OracleFeatureDataAccess> _logger;

    public OracleFeatureDataAccess(
        IOracleConnectionFactory connectionFactory,
        IOptions<OracleOptions> options,
        ILogger<OracleFeatureDataAccess> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        OracleLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("oracle.feature.select");
        activity?.SetTag("db.system", "oracle");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("layer.id", mapping.LayerId);

        await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, query);

        var features = ImmutableArray.CreateBuilder<Feature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                // Ordinal 0 is the configured primary-key/object-id column. A NULL here means the
                // mapped PrimaryKeyColumn is not a true key (or is mis-mapped); silently coercing
                // to id 0 would collapse every such row onto a single feature id and hide the
                // misconfiguration. Surface it instead.
                throw new InvalidOperationException(
                    $"Oracle layer {mapping.LayerId} returned a NULL primary key for column '{mapping.PrimaryKeyColumn}'; " +
                    "the configured PrimaryKeyColumn must map to a non-nullable identifier.");
            }

            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var wkb = reader.IsDBNull(1) ? null : ReadWkb(reader, 1);

            var attrs = ImmutableDictionary<string, object?>.Empty.ToBuilder();
            for (var i = 2; i < reader.FieldCount; i++)
            {
                // The builder emits attribute columns as double-quoted (case-preserving)
                // identifiers, so reader.GetName(i) returns the exact configured column name
                // for the column actually projected at this ordinal. A positional remap onto
                // the full attributeColumns list would mis-key values whenever OutFields
                // restricts the projection to a subset: the builder skips non-requested
                // columns (preserving order), so the SQL column at ordinal i and
                // attributeColumns[i-2] would refer to different columns.
                var name = reader.GetName(i);
                attrs[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            features.Add(Feature.Create(id, wkb, attrs.ToImmutable()));
        }

        return features.ToImmutable();
    }

    public async Task<long> ExecuteCountAsync(
        OracleLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("oracle.feature.count");
        activity?.SetTag("db.system", "oracle");
        activity?.SetTag("db.operation", "count");
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
        OracleLayerMapping mapping,
        ParameterizedQuery query,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("oracle.feature.extent");
        activity?.SetTag("db.system", "oracle");
        activity?.SetTag("db.operation", "extent");
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
        using var activity = _activitySource.StartActivity("oracle.feature.objectids");
        activity?.SetTag("db.system", "oracle");
        activity?.SetTag("db.operation", "objectids");

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

    private OracleCommand CreateCommand(OracleConnection connection, ParameterizedQuery query)
    {
        var command = connection.CreateCommand();
        // BindByName is required for the colon-prefixed :p<N> placeholders the builder emits;
        // ODP.NET binds positionally by default, which would silently mis-order parameters.
        command.BindByName = true;
        command.CommandText = query.Sql;
        command.CommandTimeout = _options.Value.CommandTimeoutSeconds;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var name = "p" + i.ToString(CultureInfo.InvariantCulture);
            var value = query.WhereParameters[i] ?? DBNull.Value;
            command.Parameters.Add(new OracleParameter(name, value));
        }

        OracleFeatureLog.QueryPrepared(_logger, query.Sql, query.WhereParameters.Count);
        return command;
    }

    private static byte[]? ReadWkb(OracleDataReader reader, int ordinal)
    {
        // SDO_UTIL.TO_WKBGEOMETRY returns a BLOB; ODP.NET surfaces it as OracleBlob when the
        // default LOB fetch size is in effect. Materialize the bytes eagerly so the caller
        // (which formats into protocol responses) does not need to know about the LOB stream.
        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            byte[] bytes => bytes,
            OracleBlob blob => ReadBlob(blob),
            _ => null
        };
    }

    private static byte[] ReadBlob(OracleBlob blob)
    {
        try
        {
            var length = blob.Length;
            if (length == 0)
            {
                return [];
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = blob.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    // ODP.NET stops returning bytes when the LOB is exhausted; treat as end-of-stream.
                    break;
                }

                offset += read;
            }

            if (offset == buffer.Length)
            {
                return buffer;
            }

            var trimmed = new byte[offset];
            Array.Copy(buffer, trimmed, offset);
            return trimmed;
        }
        finally
        {
            blob.Dispose();
        }
    }
}
