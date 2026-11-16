// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
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
using Honua.Core.Queries.Filters;
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

    private sealed record ManagedStoreIdentitySnapshot(string? Value);

    private string ResolveManagedStoreTableName() => _configuredFeatureSchema is null
        ? ManagedFeaturesTableName
        : $"{SqlIdentifier.QuoteIdentifier(_configuredFeatureSchema)}.{SqlIdentifier.QuoteIdentifier(ManagedFeaturesTableName)}";

    private async Task<ManagedStoreIdentitySnapshot> CaptureManagedStoreIdentityAsync(CancellationToken cancellationToken)
    {
        if (_featureStoreConnections is null)
        {
            return new ManagedStoreIdentitySnapshot(null);
        }
        var serverConnection = await _featureStoreConnections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (serverConnection.ConfigureAwait(false))
        {
            await using var command = serverConnection.CreateCommand();
            command.CommandText = ManagedStoreIdentitySql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "managedTable";
            parameter.Value = ResolveManagedStoreTableName();
            command.Parameters.Add(parameter);
            return new ManagedStoreIdentitySnapshot(
                await ReadManagedStoreIdentityAsync(command, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task VerifyManagedStoreConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken,
        ManagedStoreIdentitySnapshot? capturedIdentity = null)
    {
        if (_featureStoreConnections is null)
        {
            return;
        }

        var managedTable = ResolveManagedStoreTableName();

        string? publishIdentity;
        await using (var command = new NpgsqlCommand(ManagedStoreIdentitySql, connection, transaction))
        {
            command.Parameters.AddWithValue("managedTable", managedTable);
            publishIdentity = await ReadManagedStoreIdentityAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var serverIdentity = (capturedIdentity ?? await CaptureManagedStoreIdentityAsync(cancellationToken).ConfigureAwait(false)).Value;

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

    private sealed record PublishedLayerStorage(
        string Schema,
        string? BindingSchemaName,
        string Table,
        string PrimaryKeyColumn,
        string? GeometryColumn,
        int StorageSrid,
        string StorageOptionsJson,
        bool IsManagedStore,
        IReadOnlyList<string>? SmallintColumns = null)
    {
        public static PublishedLayerStorage ForSourceTable(
            string schema,
            string table,
            string primaryKeyColumn,
            string? geometryColumn,
            int storageSrid,
            IReadOnlyList<string>? smallintColumns = null)
            => new(
                schema,
                schema,
                table,
                primaryKeyColumn,
                geometryColumn,
                storageSrid,
                SourceBackedStorageOptionsJson,
                IsManagedStore: false,
                SmallintColumns: smallintColumns);

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
