// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: honua.* catalog persistence.
//
// All transactional writes against the honua catalog: locating/inserting honua.layers,
// inserting honua.layer_fields, ensuring honua.services + honua.service_layers wiring,
// resolving persisted data_connections, advancing the layer_id sequence, materializing
// features into the canonical features table, and the small read paths that hydrate
// PublishedLayerSummary (single-layer and by-id). Also hosts SetLayerEnabledCoreAsync and
// the CloneWithEnabled DTO helper used by the enable/disable surface. Grouped here so the
// catalog-mutating SQL stays together and is easy to review for transactional consistency.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    /// <summary>
    /// Serializes concurrent publishes of the same source table for the duration of
    /// the transaction. honua.layers has no unique constraint on
    /// (table_schema, table_name), so without this two concurrent publishes (e.g.
    /// parallel AutoPublish imports) could both pass <see cref="FindExistingLayerAsync"/>
    /// and insert duplicate layer rows. The transaction-scoped advisory lock is
    /// released automatically at commit/rollback; the loser then re-runs the
    /// existence check against the committed row and surfaces the Conflict error.
    /// </summary>
    private static async Task AcquireLayerPublishLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(
                hashtextextended('honua.layers:' || lower(@schema) || '.' || lower(@table), 0));
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int?> FindExistingLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT layer_id
            FROM honua.layers
            WHERE table_schema = @schema AND table_name = @table;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : null;
    }

    private static async Task<int> InsertLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string layerName,
        string? description,
        string schema,
        string table,
        string primaryKeyColumn,
        string? geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        LayerExtentInsert? extent,
        bool enabled,
        string storageOptionsJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.layers (
                layer_name,
                description,
                table_schema,
                table_name,
                primary_key_column,
                geometry_column,
                storage_options,
                storage_srid,
                geometry_type,
                srid,
                extent,
                default_visibility,
                enabled
            )
            VALUES (
                @name,
                @description,
                @schema,
                @table,
                @primaryKeyColumn,
                @geometryColumn,
                @storageOptions,
                @storageSrid,
                @geometryType,
                @srid,
                CASE
                    WHEN @extentMinX IS NULL THEN NULL
                    ELSE ST_MakeEnvelope(@extentMinX, @extentMinY, @extentMaxX, @extentMaxY, @extentSrid)
                END,
                TRUE,
                @enabled
            )
            RETURNING layer_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@name", layerName);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@primaryKeyColumn", primaryKeyColumn);
        command.Parameters.Add("@geometryColumn", NpgsqlDbType.Text).Value = (object?)geometryColumn ?? DBNull.Value;
        command.Parameters.Add("@storageOptions", NpgsqlDbType.Jsonb).Value = storageOptionsJson;
        command.Parameters.AddWithValue("@geometryType", geometryType);
        command.Parameters.AddWithValue("@srid", srid);
        command.Parameters.AddWithValue("@storageSrid", storageSrid);
        AddNullableDouble(command, "@extentMinX", extent?.MinX);
        AddNullableDouble(command, "@extentMinY", extent?.MinY);
        AddNullableDouble(command, "@extentMaxX", extent?.MaxX);
        AddNullableDouble(command, "@extentMaxY", extent?.MaxY);
        command.Parameters.AddWithValue("@extentSrid", extent?.Srid ?? CatalogExtentSrid);
        command.Parameters.AddWithValue("@enabled", enabled);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not int layerId)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Unknown,
                "Failed to create layer.");
        }

        return layerId;
    }

    private static async Task EnsureLayerSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH layer_state AS (
                SELECT COALESCE(MAX(layer_id), 0) AS max_layer_id
                FROM honua.layers
            ),
            sequence_state AS (
                SELECT last_value, is_called
                FROM honua.layers_layer_id_seq
            )
            SELECT setval(
                pg_get_serial_sequence('honua.layers', 'layer_id'),
                GREATEST(layer_state.max_layer_id, sequence_state.last_value),
                CASE
                    WHEN layer_state.max_layer_id = 0 AND sequence_state.is_called = FALSE THEN FALSE
                    ELSE TRUE
                END
            )
            FROM layer_state
            CROSS JOIN sequence_state;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> CountSourceRowsAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var sourceTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(BuildSourceCountSql(sourceTable), connection)
            {
                CommandTimeout = 30
            };
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is long count ? count : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.NotFound,
                $"Source table '{schema}.{table}' was not found.");
        }
    }

    private async Task<int> MaterializeLayerFeaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        string schema,
        string table,
        string? geometryColumn,
        int srid,
        IReadOnlyList<ColumnInfo> attributeColumns,
        string? featuresSchema,
        CancellationToken cancellationToken)
    {
        // TODO(honua-server#974): replace this snapshot copy with the settled
        // publish refresh/CDC path once source-of-truth policy is finalized.
        // The copy stays inside the publication transaction. Tables, partitioned
        // tables, and materialized views page by (tableoid, ctid) so a gateway
        // timeout is not what bounds the statement (honua-server#5948). Views have
        // no ctid and keep the single INSERT...SELECT.
        var sourceTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var canonicalGeometry = string.IsNullOrWhiteSpace(geometryColumn)
            ? "NULL::geometry"
            : BuildCanonicalGeometryExpression($"src.{QuoteIdentifier(geometryColumn)}");
        var attributesExpression = BuildAttributesExpression(attributeColumns);

        // A managed-store publish names the exact features table its binding reads; the
        // source snapshot keeps resolving the table through the connection search path.
        var featuresTable = featuresSchema is null
            ? await ResolveCanonicalFeaturesTableAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false)
            : $"{QuoteIdentifier(featuresSchema)}.{QuoteIdentifier(ManagedFeaturesTableName)}";

        if (!await SourceSupportsKeysetAsync(connection, transaction, schema, table, cancellationToken)
                .ConfigureAwait(false))
        {
            var unbounded = BuildUnboundedSnapshotSql(
                featuresTable, sourceTable, canonicalGeometry, attributesExpression);
            await using var command = new NpgsqlCommand(unbounded, connection, transaction);
            command.Parameters.AddWithValue("layerId", layerId);
            command.Parameters.AddWithValue("srid", srid);
            var result = await ExecuteSnapshotCommandAsync(command, cancellationToken).ConfigureAwait(false);
            var inserted = result is int count
                ? count
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
            await ReportSnapshotProgressAsync(inserted, inserted, layerId, cancellationToken).ConfigureAwait(false);
            return inserted;
        }

        var pageSql = BuildSnapshotPageSql(
            featuresTable, sourceTable, canonicalGeometry, attributesExpression);
        return await ExecuteUnderSnapshotBudgetAsync(
            connection,
            transaction,
            async (budgetToken, commandTimeout) =>
            {
                var total = await ExecuteSourceCountAsync(
                    connection, transaction, sourceTable, commandTimeout, budgetToken).ConfigureAwait(false);
                var materialized = await CopySnapshotPagesAsync(
                    _materializationBatchSize,
                    (hasCursor, tableOid, ctid, pageToken) => ExecuteSnapshotPageAsync(
                        connection,
                        transaction,
                        pageSql,
                        layerId,
                        srid,
                        hasCursor,
                        tableOid,
                        ctid,
                        commandTimeout,
                        pageToken),
                    (done, rowsTotal, reportToken) => ReportSnapshotProgressAsync(done, rowsTotal, layerId, reportToken),
                    total,
                    budgetToken).ConfigureAwait(false);
                if (materialized > int.MaxValue)
                {
                    throw new LayerPublishingException(
                        LayerPublishingErrorKind.Unknown,
                        "Layer snapshot exceeds the supported feature count.");
                }

                return (int)materialized;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One keyset page of a canonical snapshot copy.</summary>
    internal readonly record struct SnapshotPage(int Inserted, uint? TableOid, string? Ctid);

    /// <summary>
    /// Copies snapshot pages until a short or empty page. A full page without a cursor fails
    /// rather than looping. <paramref name="report"/> runs after every page, including the last.
    /// </summary>
    internal static async Task<long> CopySnapshotPagesAsync(
        int batchSize,
        Func<bool, uint, string?, CancellationToken, Task<SnapshotPage>> copyPage,
        Func<long, long, CancellationToken, Task>? report,
        long rowsTotal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copyPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        long materialized = 0;
        var hasCursor = false;
        uint tableOid = 0;
        string? ctid = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await copyPage(hasCursor, tableOid, ctid, cancellationToken).ConfigureAwait(false);
            if (page.Inserted < 0)
            {
                throw new InvalidOperationException("Snapshot page returned a negative row count.");
            }

            materialized += page.Inserted;
            if (report is not null)
            {
                await report(materialized, rowsTotal, cancellationToken).ConfigureAwait(false);
            }

            if (page.Inserted == 0 || page.Inserted < batchSize)
            {
                return materialized;
            }

            if (page.TableOid is null || string.IsNullOrWhiteSpace(page.Ctid))
            {
                throw new InvalidOperationException(
                    "Snapshot page returned a full batch without a (tableoid, ctid) cursor.");
            }

            if (hasCursor
                && page.TableOid.Value == tableOid
                && string.Equals(page.Ctid, ctid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Snapshot page cursor did not advance.");
            }

            hasCursor = true;
            tableOid = page.TableOid.Value;
            ctid = page.Ctid;
        }
    }

    /// <summary>Single-statement snapshot copy used for views and unknown relkinds.</summary>
    internal static string BuildUnboundedSnapshotSql(
        string featuresTable,
        string sourceTable,
        string canonicalGeometry,
        string attributesExpression)
        => $"""
            WITH inserted AS (
                INSERT INTO {featuresTable} (layer_id, geometry, attributes)
                SELECT
                    @layerId,
                    {canonicalGeometry},
                    {attributesExpression}
                FROM {sourceTable} AS src
                RETURNING 1
            )
            SELECT COUNT(*)::int
            FROM inserted;
            """;

    /// <summary>
    /// One keyset page. <c>page</c> is materialized once so the insert and the cursor read
    /// the same rows. <c>@ctid</c> is null on the first page; a null tid cast does not fail.
    /// </summary>
    internal static string BuildSnapshotPageSql(
        string featuresTable,
        string sourceTable,
        string canonicalGeometry,
        string attributesExpression)
        => $"""
            WITH page AS MATERIALIZED (
                SELECT
                    src.tableoid AS source_oid,
                    src.ctid AS source_tid,
                    {canonicalGeometry} AS geometry,
                    {attributesExpression} AS attributes
                FROM {sourceTable} AS src
                WHERE @hasCursor = FALSE
                    OR (src.tableoid, src.ctid) > (@tableOid, @ctid::tid)
                ORDER BY src.tableoid, src.ctid
                LIMIT @batchSize
            ),
            inserted AS (
                INSERT INTO {featuresTable} (layer_id, geometry, attributes)
                SELECT @layerId, page.geometry, page.attributes
                FROM page
                RETURNING 1
            )
            SELECT
                (SELECT COUNT(*)::int FROM inserted),
                (SELECT source_oid::bigint FROM page ORDER BY source_oid DESC, source_tid DESC LIMIT 1),
                (SELECT source_tid::text FROM page ORDER BY source_oid DESC, source_tid DESC LIMIT 1);
            """;

    internal static string BuildSourceCountSql(string sourceTable)
        => $"SELECT COUNT(*)::bigint FROM {sourceTable} AS src;";

    private async Task ReportSnapshotProgressAsync(
        long rowsMaterialized,
        long rowsTotal,
        int layerId,
        CancellationToken cancellationToken)
    {
        Log.SnapshotBatchMaterialized(_logger, rowsMaterialized, rowsTotal, layerId);
        var progress = LayerSnapshotProgressScope.Current;
        if (progress is null)
        {
            return;
        }

        try
        {
            await progress.ReportAsync(rowsMaterialized, rowsTotal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.SnapshotProgressFailed(_logger, layerId, exception);
        }
    }

    private static async Task<bool> SourceSupportsKeysetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = 30
        };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var relkind = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return relkind is "r" or "p" or "m";
    }

    private static async Task<long> ExecuteSourceCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceTable,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(BuildSourceCountSql(sourceTable), connection, transaction)
        {
            CommandTimeout = commandTimeout
        };
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? count : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<SnapshotPage> ExecuteSnapshotPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int layerId,
        int srid,
        bool hasCursor,
        uint tableOid,
        string? ctid,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = commandTimeout
        };
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("srid", srid);
        command.Parameters.Add(new NpgsqlParameter("hasCursor", NpgsqlDbType.Boolean) { Value = hasCursor });
        command.Parameters.Add(new NpgsqlParameter("tableOid", NpgsqlDbType.Oid)
        {
            Value = hasCursor ? tableOid : 0u
        });
        command.Parameters.Add(new NpgsqlParameter("ctid", NpgsqlDbType.Text)
        {
            Value = hasCursor ? ctid ?? string.Empty : DBNull.Value
        });
        command.Parameters.AddWithValue("batchSize", _materializationBatchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SnapshotPage(0, null, null);
        }

        var inserted = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        uint? sourceOid = null;
        if (!reader.IsDBNull(1))
        {
            var oid = reader.GetInt64(1);
            if (oid < 0 || oid > uint.MaxValue)
            {
                throw new InvalidOperationException("Snapshot page returned a tableoid outside the oid range.");
            }

            sourceOid = (uint)oid;
        }

        var sourceTid = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new SnapshotPage(inserted, sourceOid, sourceTid);
    }

    /// <summary>
    /// Runs the keyset copy under one publication budget. Statement timeout is set once,
    /// transaction-local, for every page.
    /// </summary>
    private async Task<T> ExecuteUnderSnapshotBudgetAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Func<CancellationToken, int, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var commandTimeout = _materializationTimeoutSeconds + SnapshotCommandTimeoutGraceSeconds;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_materializationTimeoutSeconds));
        try
        {
            await using var serverTimeout = new NpgsqlCommand(
                "SELECT set_config('statement_timeout', @timeout, true)", connection, transaction)
            {
                CommandTimeout = commandTimeout
            };
            serverTimeout.Parameters.AddWithValue("timeout",
                FormattableString.Invariant($"{commandTimeout}s"));
            await serverTimeout.ExecuteScalarAsync(budget.Token).ConfigureAwait(false);
            return await action(budget.Token, commandTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Layer snapshot materialization exceeded its {_materializationTimeoutSeconds}-second budget "
                + $"({LayerPublishingOptions.SectionName}:MaterializationTimeoutSeconds). The publication transaction "
                + "rolls back; the source table is retained.", exception);
        }
    }

    /// <summary>Seconds the driver read timeout extends past the snapshot budget.</summary>
    internal const int SnapshotCommandTimeoutGraceSeconds = 30;

    /// <summary>
    /// Runs one canonical snapshot command (copy or pre-refresh delete) under the publication
    /// budget instead of the connection's ordinary command timeout. The command stays inside the
    /// caller's transaction, so expiry or caller cancellation leaves it to roll back; the source
    /// table is never modified.
    /// </summary>
    internal async Task<object?> ExecuteSnapshotCommandAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        if (command.Transaction is null)
        {
            throw new InvalidOperationException("Snapshot commands require a publication transaction.");
        }

        // The wall-clock budget below is the bound. The driver's read timeout is only a backstop,
        // set past the budget so expiry surfaces as a cooperative cancellation (connection stays
        // usable for the rollback) rather than a driver timeout that may break the connection.
        command.CommandTimeout = _materializationTimeoutSeconds + SnapshotCommandTimeoutGraceSeconds;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_materializationTimeoutSeconds));

        try
        {
            // Startup options may impose a shorter server timeout even when the driver's
            // timeout is extended. Keep the override local to the publication transaction.
            await using var serverTimeout = new NpgsqlCommand(
                "SELECT set_config('statement_timeout', @timeout, true)", command.Connection, command.Transaction)
            {
                CommandTimeout = command.CommandTimeout
            };
            serverTimeout.Parameters.AddWithValue("timeout",
                FormattableString.Invariant($"{command.CommandTimeout}s"));
            await serverTimeout.ExecuteScalarAsync(budget.Token).ConfigureAwait(false);
            return await command.ExecuteScalarAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Layer snapshot materialization exceeded its {_materializationTimeoutSeconds}-second budget "
                + $"({LayerPublishingOptions.SectionName}:MaterializationTimeoutSeconds). The publication transaction "
                + "rolls back; the source table is retained.", exception);
        }
    }

    private static async Task<bool> ServiceExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM honua.services
                WHERE service_name = @serviceName
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }

    private static async Task<List<int>> ListServiceLayerIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT layer_id
            FROM honua.service_layers
            WHERE service_name = @serviceName
            ORDER BY layer_order, layer_id;
            """;

        var layerIds = new List<int>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            layerIds.Add(reader.GetInt32(0));
        }

        return layerIds;
    }

    private static async Task<int?> ResolveExistingServiceSridAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HonuaTableExistsAsync(connection, "services", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        const string sql = """
            SELECT srid
            FROM honua.services
            WHERE service_name = @serviceName
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task InsertFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        List<LayerFieldInsert> fields,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.layer_fields (
                layer_id,
                field_name,
                field_type,
                field_order,
                max_length,
                nullable,
                default_value,
                description
            )
            VALUES (@layerId, @fieldName, @fieldType, @fieldOrder, @maxLength, @nullable, @defaultValue, @description);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var layerIdParameter = command.Parameters.Add("@layerId", NpgsqlDbType.Integer);
        var fieldNameParameter = command.Parameters.Add("@fieldName", NpgsqlDbType.Varchar);
        var fieldTypeParameter = command.Parameters.Add("@fieldType", NpgsqlDbType.Varchar);
        var fieldOrderParameter = command.Parameters.Add("@fieldOrder", NpgsqlDbType.Integer);
        var maxLengthParameter = command.Parameters.Add("@maxLength", NpgsqlDbType.Integer);
        var nullableParameter = command.Parameters.Add("@nullable", NpgsqlDbType.Boolean);
        var defaultValueParameter = command.Parameters.Add("@defaultValue", NpgsqlDbType.Text);
        var descriptionParameter = command.Parameters.Add("@description", NpgsqlDbType.Text);

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            layerIdParameter.Value = layerId;
            fieldNameParameter.Value = field.Name;
            fieldTypeParameter.Value = field.Type.ToString();
            fieldOrderParameter.Value = i + 1;
            maxLengthParameter.Value = (object?)field.MaxLength ?? DBNull.Value;
            nullableParameter.Value = field.Nullable;
            defaultValueParameter.Value = (object?)field.DefaultValue ?? DBNull.Value;
            descriptionParameter.Value = (object?)field.Description ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> EnsureServiceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        int srid,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        var persistedConnectionId = await ResolvePersistedConnectionIdAsync(
            connection,
            transaction,
            connectionId,
            cancellationToken);

        const string sql = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities,
                connection_id
            )
            VALUES (@serviceName, @description, @srid, @formats, @capabilities, @connectionId)
            ON CONFLICT (service_name) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@description", $"Honua service '{serviceName}'");
        command.Parameters.AddWithValue("@srid", srid);
        command.Parameters.AddWithValue("@formats", _defaultFormats);
        command.Parameters.AddWithValue("@capabilities", _defaultCapabilities);
        command.Parameters.AddWithValue("@connectionId", (object?)persistedConnectionId ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<Guid?> ResolvePersistedConnectionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        if (!connectionId.HasValue)
        {
            return null;
        }

        const string tableExistsSql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'honua'
                  AND table_name = 'data_connections'
            );
            """;

        await using (var tableCommand = new NpgsqlCommand(tableExistsSql, connection, transaction))
        {
            var tableExists = (bool?)await tableCommand.ExecuteScalarAsync(cancellationToken) ?? false;
            if (!tableExists)
            {
                return null;
            }
        }

        const string connectionExistsSql = """
            SELECT EXISTS (
                SELECT 1
                FROM honua.data_connections
                WHERE connection_id = @connectionId
            );
            """;

        await using var connectionCommand = new NpgsqlCommand(connectionExistsSql, connection, transaction);
        connectionCommand.Parameters.AddWithValue("@connectionId", connectionId.Value);
        var connectionExists = (bool?)await connectionCommand.ExecuteScalarAsync(cancellationToken) ?? false;
        return connectionExists ? connectionId : null;
    }

    private static async Task EnsureServiceLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string orderSql = """
            SELECT COALESCE(MAX(layer_order), 0) + 1
            FROM honua.service_layers
            WHERE service_name = @serviceName;
            """;

        await using var orderCommand = new NpgsqlCommand(orderSql, connection, transaction);
        orderCommand.Parameters.AddWithValue("@serviceName", serviceName);
        var orderResult = await orderCommand.ExecuteScalarAsync(cancellationToken);
        var nextOrder = Convert.ToInt32(orderResult, CultureInfo.InvariantCulture);

        const string insertSql = """
            INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
            VALUES (@serviceName, @layerId, @layerOrder)
            ON CONFLICT (service_name, layer_id) DO NOTHING;
            """;

        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("@serviceName", serviceName);
        insertCommand.Parameters.AddWithValue("@layerId", layerId);
        insertCommand.Parameters.AddWithValue("@layerOrder", nextOrder);

        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PublishedLayerSummary?> GetLayerSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled,
                COUNT(f.field_name)::int AS field_count
            FROM honua.layers l
            INNER JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id
            LEFT JOIN honua.layer_fields f
                ON f.layer_id = l.layer_id
            WHERE sl.service_name = @serviceName
                AND l.layer_id = @layerId
            GROUP BY
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublishedLayerSummary
        {
            LayerId = reader.GetInt32(0),
            LayerName = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Schema = reader.GetString(3),
            Table = reader.GetString(4),
            PrimaryKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            GeometryType = reader.GetString(6),
            Srid = reader.GetInt32(7),
            Enabled = reader.GetBoolean(8),
            FieldCount = reader.GetInt32(9),
            ServiceName = serviceName
        };
    }

    private static async Task<PublishedLayerSummary?> GetLayerSummaryByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled,
                COUNT(f.field_name)::int AS field_count
            FROM honua.layers l
            LEFT JOIN honua.layer_fields f
                ON f.layer_id = l.layer_id
            WHERE l.layer_id = @layerId
            GROUP BY
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublishedLayerSummary
        {
            LayerId = reader.GetInt32(0),
            LayerName = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Schema = reader.GetString(3),
            Table = reader.GetString(4),
            PrimaryKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            GeometryType = reader.GetString(6),
            Srid = reader.GetInt32(7),
            Enabled = reader.GetBoolean(8),
            FieldCount = reader.GetInt32(9),
            ServiceName = DefaultServiceName
        };
    }

    private static async Task SetLayerEnabledCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE honua.layers
            SET enabled = @enabled
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@enabled", enabled);
        command.Parameters.AddWithValue("@layerId", layerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PublishedLayerSummary CloneWithEnabled(PublishedLayerSummary layer, bool enabled)
    {
        return new PublishedLayerSummary
        {
            LayerId = layer.LayerId,
            LayerName = layer.LayerName,
            Description = layer.Description,
            Schema = layer.Schema,
            Table = layer.Table,
            GeometryType = layer.GeometryType,
            Srid = layer.Srid,
            PrimaryKey = layer.PrimaryKey,
            FieldCount = layer.FieldCount,
            Enabled = enabled,
            ServiceName = layer.ServiceName,
            License = layer.License,
            Attribution = layer.Attribution,
            Publisher = layer.Publisher,
            LicenseUrl = layer.LicenseUrl,
            SourceUrl = layer.SourceUrl
        };
    }
}
