// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.Infrastructure;

namespace Honua.Databricks.Features.FeatureStore.Services;

/// <summary>
/// Translates Databricks SQL Statement Execution result sets into the canonical
/// feature-store domain types (features, counts, extents, object-id lists).
/// </summary>
/// <remarks>
/// "Data access" here means submitting the SQL built by
/// <see cref="DatabricksFeatureQueryBuilder"/> to the Statement Execution API through
/// <see cref="IDatabricksStatementClient"/> and decoding the JSON rows. Geometry arrives
/// as a WKB hex string and is decoded to the byte array carried by <see cref="Feature.Geometry"/>.
/// </remarks>
internal interface IDatabricksFeatureDataAccess
{
    /// <summary>Executes a SELECT statement and projects the rows into features.</summary>
    Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        DatabricksLayerMapping mapping,
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken);

    /// <summary>Executes a COUNT statement and returns the scalar count.</summary>
    Task<long> ExecuteCountAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken);

    /// <summary>Executes an extent statement and returns the bounding box, if any.</summary>
    Task<FeatureExtent?> ExecuteExtentAsync(
        DatabricksLayerMapping mapping,
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken);

    /// <summary>Executes an object-id statement and returns the primary-key values.</summary>
    Task<ImmutableArray<long>> ExecuteObjectIdsAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken);

    /// <summary>
    /// Executes an aggregate statistics statement and projects each result row into a
    /// column-name-keyed dictionary (one row per group, or a single row when ungrouped).
    /// </summary>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IDatabricksFeatureDataAccess"/> implementation.
/// </summary>
internal sealed class DatabricksFeatureDataAccess : IDatabricksFeatureDataAccess
{
    private readonly IDatabricksStatementClient _client;

    public DatabricksFeatureDataAccess(IDatabricksStatementClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectAsync(
        DatabricksLayerMapping mapping,
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var resultSet = await _client.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);

        var idIndex = IndexOf(resultSet.Columns, DatabricksFeatureQueryBuilder.IdColumn);
        var geomIndex = IndexOf(resultSet.Columns, DatabricksFeatureQueryBuilder.GeometryHexColumn);

        var builder = ImmutableArray.CreateBuilder<Feature>(resultSet.Rows.Count);
        foreach (var row in resultSet.Rows)
        {
            var id = idIndex >= 0 ? ReadId(row[idIndex]) : 0;
            var geometry = geomIndex >= 0 ? DecodeWkbHex(row[geomIndex]) : null;
            var attributes = ProjectAttributes(resultSet.Columns, row, idIndex, geomIndex);
            builder.Add(Feature.Create(id, geometry, attributes));
        }

        return builder.ToImmutable();
    }

    public async Task<long> ExecuteCountAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
    {
        var resultSet = await _client.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        if (resultSet.Rows.Count == 0 || resultSet.Rows[0].Length == 0)
        {
            return 0;
        }

        return ReadId(resultSet.Rows[0][0]);
    }

    public async Task<FeatureExtent?> ExecuteExtentAsync(
        DatabricksLayerMapping mapping,
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var resultSet = await _client.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        if (resultSet.Rows.Count == 0 || resultSet.Rows[0].Length < 4)
        {
            return null;
        }

        var row = resultSet.Rows[0];
        if (TryReadDouble(row[0], out var minX)
            && TryReadDouble(row[1], out var minY)
            && TryReadDouble(row[2], out var maxX)
            && TryReadDouble(row[3], out var maxY))
        {
            return FeatureExtent.Create(minX, minY, maxX, maxY, mapping.Srid);
        }

        return null;
    }

    public async Task<ImmutableArray<long>> ExecuteObjectIdsAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
    {
        var resultSet = await _client.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        if (resultSet.Rows.Count == 0)
        {
            return ImmutableArray<long>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<long>(resultSet.Rows.Count);
        builder.AddRange(resultSet.Rows.Where(row => row.Length > 0).Select(row => ReadId(row[0])));

        return builder.ToImmutable();
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
        DatabricksSqlStatement statement,
        CancellationToken cancellationToken)
    {
        var resultSet = await _client.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        if (resultSet.Rows.Count == 0)
        {
            return ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>(resultSet.Rows.Count);
        foreach (var row in resultSet.Rows)
        {
            var dict = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < resultSet.Columns.Count && i < row.Length; i++)
            {
                dict[resultSet.Columns[i]] = ProjectValue(row[i]);
            }

            builder.Add(dict.ToImmutable());
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, object?> ProjectAttributes(
        IReadOnlyList<string> columns,
        JsonElement[] row,
        int idIndex,
        int geomIndex)
    {
        if (columns.Count == 0)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count && i < row.Length; i++)
        {
            if (i == idIndex || i == geomIndex)
            {
                continue;
            }

            builder[columns[i]] = ProjectValue(row[i]);
        }

        return builder.ToImmutable();
    }

    private static object? ProjectValue(JsonElement value)
    {
        // The Statement Execution API JSON_ARRAY disposition renders every cell as a
        // JSON string (or null). Preserve the string form; numeric coercion is left to
        // downstream formatters that know the layer's typed schema.
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var asLong) => asLong,
            JsonValueKind.Number when value.TryGetDouble(out var asDouble) => asDouble,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value.GetRawText(),
        };
    }

    private static long ReadId(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var asLong) => asLong,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out result):
                return true;
            case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result):
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static byte[]? DecodeWkbHex(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var hex = value.GetString();
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }

        // Tolerate an optional 0x / 0X prefix and odd whitespace from the warehouse.
        var span = hex.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        if (span.Length == 0 || (span.Length % 2) != 0)
        {
            return null;
        }

        return Convert.FromHexString(span);
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
