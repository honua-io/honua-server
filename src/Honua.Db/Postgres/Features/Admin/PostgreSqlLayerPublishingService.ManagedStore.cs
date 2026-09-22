// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: managed feature store publication.
//
// A layer published with LayerStorageMode.Managed copies its source rows into the shared
// managed `features` table and binds every protocol to that table, so the managed feature
// writer and the serving readers use one storage and an accepted edit reads back
// (honua-server#4859). Source-backed layers keep reading their live source table and stay
// read-only, because the managed writer cannot write through them (honua-server#4707). This
// partial holds the storage descriptor, the managed field projection and the publish-time
// capability contract that ties edit tokens to managed storage.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private const string ManagedStoreStorageOptionsJson = """{"managedStore":"true"}""";
    private const string ManagedFeaturesTableName = "features";
    private const string ManagedPrimaryKeyColumn = "objectid";
    private const string ManagedGeometryColumn = "geometry";
    private const string ManagedLayerDiscriminatorColumn = "layer_id";

    // Identifies the relation a features-table name resolves to on a connection: the
    // database, the server instance (postmaster start time) and the relation itself.
    private const string ManagedStoreIdentitySql = """
        SELECT
            database.oid::bigint,
            pg_postmaster_start_time(),
            to_regclass(@managedTable)::oid::bigint
        FROM pg_database AS database
        WHERE database.datname = current_database();
        """;
    private const string SourceStorageModeName = "source";
    private const string ManagedStorageModeName = "managed";
    private const string QueryCapability = "Query";

    private static readonly string[] _publicationCapabilityTokens =
    [
        QueryCapability,
        "Extract",
        MetadataV2EditCapabilities.Create,
        MetadataV2EditCapabilities.Update,
        MetadataV2EditCapabilities.Delete,
        MetadataV2EditCapabilities.Editing
    ];

    private static readonly string[] _editCapabilityTokens =
    [
        MetadataV2EditCapabilities.Create,
        MetadataV2EditCapabilities.Update,
        MetadataV2EditCapabilities.Delete,
        MetadataV2EditCapabilities.Editing
    ];

    /// <summary>
    /// Resolves the capability tokens declared on a published layer's feature publication.
    /// </summary>
    /// <param name="requested">Tokens supplied by the caller; null or empty keeps the read-only default.</param>
    /// <param name="managedStore">Whether the layer is published into the managed feature store.</param>
    /// <returns>Canonically cased, de-duplicated capability tokens.</returns>
    /// <exception cref="LayerPublishingException">An unknown token, a set without Query, or an
    /// edit token on a source-backed layer. Declaring an edit the storage cannot service would
    /// only move the refusal from publish time to the first write (a 501).</exception>
    private static IReadOnlyList<string> ResolvePublicationCapabilities(
        IReadOnlyList<string>? requested,
        bool managedStore)
    {
        if (requested is null || requested.Count == 0)
        {
            return _defaultCapabilities;
        }

        var resolved = new List<string>(requested.Count);
        foreach (var trimmed in requested.Select(token => token?.Trim()))
        {
            var canonical = string.IsNullOrEmpty(trimmed)
                ? null
                : Array.Find(
                    _publicationCapabilityTokens,
                    known => known.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Validation,
                    $"Capability '{trimmed}' is not supported. Supported capabilities: {string.Join(", ", _publicationCapabilityTokens)}.");
            }

            if (!resolved.Contains(canonical))
            {
                resolved.Add(canonical);
            }
        }

        if (!resolved.Contains(QueryCapability))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Capabilities must include Query.");
        }

        if (!managedStore && resolved.Exists(capability => Array.IndexOf(_editCapabilityTokens, capability) >= 0))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Edit capabilities (Create, Update, Delete, Editing) require storageMode managed. A layer served from its source table cannot be edited through the server.");
        }

        return resolved;
    }

    /// <summary>
    /// Builds the schema of a managed-store layer. The managed store assigns its own object
    /// ids, so the identity field is the managed table's key; the source key is carried as an
    /// ordinary nullable attribute (features created through the server do not have one)
    /// unless it is itself named objectid, in which case the managed id replaces it.
    /// </summary>
    private static List<LayerFieldInsert> BuildManagedLayerFields(
        List<ColumnInfo> selectedColumns,
        ColumnInfo? sourcePrimaryKeyColumn,
        string? sourceGeometryColumn,
        IReadOnlyDictionary<string, MetadataV2FieldDomain> fieldDomains)
    {
        var fields = new List<LayerFieldInsert>
        {
            new(ManagedPrimaryKeyColumn, MetadataV2FieldType.Integer, null, false, null)
        };

        foreach (var column in SelectManagedAttributeColumns(selectedColumns, sourcePrimaryKeyColumn, sourceGeometryColumn))
        {
            var isSourceKey = IsSourceKey(column, sourcePrimaryKeyColumn);
            fields.Add(new LayerFieldInsert(
                column.Name,
                MapPostgresType(column.DataType),
                column.MaxLength,
                isSourceKey || column.IsNullable,
                null,
                Domain: fieldDomains.TryGetValue(column.Name, out var domain) ? domain : null));
        }

        fields.Add(new LayerFieldInsert(
            ManagedGeometryColumn,
            MetadataV2FieldType.Geometry,
            null,
            true,
            "Geometry"));

        return fields;
    }

    /// <summary>
    /// Selects the source columns copied into the managed store's JSONB attributes: every
    /// selected non-geometry column except a source key named objectid. Any other column that
    /// collides with a managed-store column name is refused rather than silently dropped.
    /// </summary>
    private static List<ColumnInfo> SelectManagedAttributeColumns(
        List<ColumnInfo> selectedColumns,
        ColumnInfo? sourcePrimaryKeyColumn,
        string? sourceGeometryColumn)
    {
        var attributes = new List<ColumnInfo>(selectedColumns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in selectedColumns)
        {
            if (string.Equals(column.Name, sourceGeometryColumn, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(column.Name))
            {
                continue;
            }

            if (string.Equals(column.Name, ManagedPrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Name, ManagedGeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                if (IsSourceKey(column, sourcePrimaryKeyColumn))
                {
                    continue;
                }

                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Validation,
                    $"Field '{column.Name}' uses a column name the managed feature store reserves. Leave it out of fields to publish into managed storage.");
            }

            attributes.Add(column);
        }

        return attributes;
    }

    private static bool IsSourceKey(ColumnInfo column, ColumnInfo? sourcePrimaryKeyColumn)
        => sourcePrimaryKeyColumn is not null
            && string.Equals(column.Name, sourcePrimaryKeyColumn.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records the primary-key validation check for a managed-store publication. The managed
    /// store assigns object ids, so the source key is optional and may be of any type; only a
    /// named key that does not exist on the source table is an error.
    /// </summary>
    private static string? ResolveManagedSourceKeyForValidation(
        List<ColumnInfo> columns,
        string? requestedPrimaryKey,
        string? primaryKeyName,
        List<TablePublishValidationCheck> checks)
    {
        var requested = requestedPrimaryKey?.Trim();
        if (!string.IsNullOrEmpty(requested)
            && !columns.Exists(column => string.Equals(column.Name, requested, StringComparison.OrdinalIgnoreCase)))
        {
            checks.Add(Error(
                "primary-key",
                $"Primary key field '{requested}' was not found on the source table.",
                requested,
                null));
            return requested;
        }

        checks.Add(Pass(
            "primary-key",
            "Managed-store publications assign their own object ids; the source key is kept as an ordinary attribute."));
        return primaryKeyName;
    }

    /// <summary>
    /// Proves the publish connection reaches the managed features table the server's own
    /// feature writer writes. The copy runs on the publish connection while reads and edits
    /// use the server's connection; if the two resolve different tables, the layer would
    /// serve none of its copied rows and its edits would land elsewhere.
    /// </summary>
    /// <exception cref="LayerPublishingException">The connections resolve different tables.</exception>
    private async Task VerifyManagedStoreConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (_featureStoreConnections is null)
        {
            return;
        }

        var managedTable = _configuredFeatureSchema is null
            ? ManagedFeaturesTableName
            : $"{QuoteIdentifier(_configuredFeatureSchema)}.{QuoteIdentifier(ManagedFeaturesTableName)}";

        string? publishIdentity;
        await using (var command = new NpgsqlCommand(ManagedStoreIdentitySql, connection, transaction))
        {
            command.Parameters.AddWithValue("managedTable", managedTable);
            publishIdentity = await ReadManagedStoreIdentityAsync(command, cancellationToken).ConfigureAwait(false);
        }

        string? serverIdentity;
        var serverConnection = await _featureStoreConnections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (serverConnection.ConfigureAwait(false))
        {
            await using var command = serverConnection.CreateCommand();
            command.CommandText = ManagedStoreIdentitySql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "managedTable";
            parameter.Value = managedTable;
            command.Parameters.Add(parameter);
            serverIdentity = await ReadManagedStoreIdentityAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (publishIdentity is null || !string.Equals(publishIdentity, serverIdentity, StringComparison.Ordinal))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "storageMode managed needs a connection to the server's own feature database; this connection does not reach the managed features table the server writes.");
        }
    }

    private static async Task<string?> ReadManagedStoreIdentityAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(2))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{reader.GetInt64(0)}:{reader.GetFieldValue<DateTime>(1):O}:{reader.GetInt64(2)}");
    }

    /// <summary>
    /// Physical storage a published layer is recorded against in honua.layers and bound to in
    /// the Metadata v2 graph. <see cref="BindingSchemaName"/> is the schema the storage binding
    /// names: the source schema for a source-backed layer; for a managed-store layer, the
    /// schema the managed feature writer qualifies its table with, or null when the writer
    /// uses the server connection's search path.
    /// </summary>
    private sealed record PublishedLayerStorage(
        string Schema,
        string? BindingSchemaName,
        string Table,
        string PrimaryKeyColumn,
        string? GeometryColumn,
        int StorageSrid,
        string StorageOptionsJson,
        bool IsManagedStore)
    {
        public static PublishedLayerStorage ForSourceTable(
            string schema,
            string table,
            string primaryKeyColumn,
            string? geometryColumn,
            int storageSrid)
            => new(
                schema,
                schema,
                table,
                primaryKeyColumn,
                geometryColumn,
                storageSrid,
                SourceBackedStorageOptionsJson,
                IsManagedStore: false);

        // Materialization transforms every geometry to the layer SRID, so managed rows are
        // stored in it.
        public static PublishedLayerStorage ForManagedStore(
            string featuresSchema,
            string? writerSchema,
            int layerSrid)
            => new(
                featuresSchema,
                writerSchema,
                ManagedFeaturesTableName,
                ManagedPrimaryKeyColumn,
                ManagedGeometryColumn,
                layerSrid,
                ManagedStoreStorageOptionsJson,
                IsManagedStore: true);
    }
}
