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

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private const string ManagedStoreStorageOptionsJson = """{"managedStore":"true"}""";
    private const string ManagedFeaturesTableName = "features";
    private const string ManagedPrimaryKeyColumn = "objectid";
    private const string ManagedGeometryColumn = "geometry";
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
        foreach (var token in requested)
        {
            var trimmed = token?.Trim();
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
        ColumnInfo sourcePrimaryKeyColumn,
        string sourceGeometryColumn,
        IReadOnlyDictionary<string, MetadataV2FieldDomain> fieldDomains)
    {
        var fields = new List<LayerFieldInsert>
        {
            new(ManagedPrimaryKeyColumn, MetadataV2FieldType.Integer, null, false, null)
        };

        foreach (var column in SelectManagedAttributeColumns(selectedColumns, sourcePrimaryKeyColumn, sourceGeometryColumn))
        {
            var isSourceKey = string.Equals(column.Name, sourcePrimaryKeyColumn.Name, StringComparison.OrdinalIgnoreCase);
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
        ColumnInfo sourcePrimaryKeyColumn,
        string sourceGeometryColumn)
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
                if (string.Equals(column.Name, sourcePrimaryKeyColumn.Name, StringComparison.OrdinalIgnoreCase))
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
        string GeometryColumn,
        int StorageSrid,
        string StorageOptionsJson,
        bool IsManagedStore)
    {
        public static PublishedLayerStorage ForSourceTable(
            string schema,
            string table,
            string primaryKeyColumn,
            string geometryColumn,
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
