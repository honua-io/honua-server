// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Security.Domain;
using Oracle.ManagedDataAccess.Client;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Default <see cref="IOracleSpatialMetadataProbe"/> implementation. Opens an Oracle connection
/// via the configured factory and queries <c>ALL_TAB_COLUMNS</c> for the geometry column type
/// and any ArcSDE versioning columns.
/// </summary>
internal sealed class OracleSpatialMetadataProbe : IOracleSpatialMetadataProbe
{
    private readonly IOracleConnectionFactory _connectionFactory;

    public OracleSpatialMetadataProbe(IOracleConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<string?> GetGeometryColumnTypeAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (string.IsNullOrWhiteSpace(mapping.GeometryColumn))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);

        var sql = new StringBuilder()
            .Append("SELECT DATA_TYPE FROM ALL_TAB_COLUMNS ")
            .Append("WHERE TABLE_NAME = UPPER(:p_table) ")
            .Append("AND COLUMN_NAME = UPPER(:p_column)");

        if (!string.IsNullOrWhiteSpace(mapping.SchemaName))
        {
            sql.Append(" AND OWNER = UPPER(:p_owner)");
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql.ToString();
        command.Parameters.Add(new OracleParameter("p_table", mapping.TableName));
        command.Parameters.Add(new OracleParameter("p_column", mapping.GeometryColumn));
        if (!string.IsNullOrWhiteSpace(mapping.SchemaName))
        {
            command.Parameters.Add(new OracleParameter("p_owner", mapping.SchemaName));
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }

        return Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        await using var connection = await _connectionFactory.OpenAsync(dataConnection, cancellationToken).ConfigureAwait(false);

        var sql = new StringBuilder()
            .Append("SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS ")
            .Append("WHERE TABLE_NAME = UPPER(:p_table) ")
            .Append("AND COLUMN_NAME IN ('GDB_FROM_DATE','GDB_TO_DATE','SDE_STATE_ID')");

        if (!string.IsNullOrWhiteSpace(mapping.SchemaName))
        {
            sql.Append(" AND OWNER = UPPER(:p_owner)");
        }

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql.ToString();
        command.Parameters.Add(new OracleParameter("p_table", mapping.TableName));
        if (!string.IsNullOrWhiteSpace(mapping.SchemaName))
        {
            command.Parameters.Add(new OracleParameter("p_owner", mapping.SchemaName));
        }

        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                matches.Add(reader.GetString(0));
            }
        }

        return matches;
    }
}
