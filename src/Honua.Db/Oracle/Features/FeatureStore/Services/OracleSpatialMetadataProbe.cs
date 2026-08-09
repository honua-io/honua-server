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

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = BuildGeometryColumnTypeSql(includeOwner: !string.IsNullOrWhiteSpace(mapping.SchemaName));
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

        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = BuildArcSdeVersioningSql(includeOwner: !string.IsNullOrWhiteSpace(mapping.SchemaName));
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

    // ALL_TAB_COLUMNS stores identifiers with their exact case (unquoted-created tables are
    // folded to upper-case by Oracle; quoted mixed/lower-case names retain their case).
    // The provider documents that operators must configure identifiers exactly as Oracle
    // stores them and the query builder emits quoted (case-preserving) identifiers, so the
    // probe compares against the configured values literally. Folding the parameter through
    // UPPER() here would silently miss valid SDO_GEOMETRY columns on tables created with
    // quoted mixed-case names (e.g. "Parcels"."Shape") and trigger the guard to reject them
    // as non-SDO. ArcSDE versioning literals stay upper-case because Esri/SDE always create
    // those columns unquoted, so Oracle stores them upper-case.
    internal static string BuildGeometryColumnTypeSql(bool includeOwner)
    {
        var sb = new StringBuilder()
            .Append("SELECT DATA_TYPE FROM ALL_TAB_COLUMNS ")
            .Append("WHERE TABLE_NAME = :p_table ")
            .Append("AND COLUMN_NAME = :p_column");

        if (includeOwner)
        {
            sb.Append(" AND OWNER = :p_owner");
        }

        return sb.ToString();
    }

    internal static string BuildArcSdeVersioningSql(bool includeOwner)
    {
        var sb = new StringBuilder()
            .Append("SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS ")
            .Append("WHERE TABLE_NAME = :p_table ")
            .Append("AND COLUMN_NAME IN ('GDB_FROM_DATE','GDB_TO_DATE','SDE_STATE_ID')");

        if (includeOwner)
        {
            sb.Append(" AND OWNER = :p_owner");
        }

        return sb.ToString();
    }
}
