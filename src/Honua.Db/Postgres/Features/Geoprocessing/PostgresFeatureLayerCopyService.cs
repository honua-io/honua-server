// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Geoprocessing;

internal sealed class PostgresFeatureLayerCopyService(
    FeatureProviderQueryRouter router,
    IAdoNetDatabaseConnectionProvider connections,
    IMetadataV2GraphStore metadata,
    ILayerPublishingService publisher,
    IFieldMaskSource? fieldMasks = null) : IFeatureLayerCopyService
{
    public async Task<FeatureLayerCopyResult> CopyAsync(int sourceLayerId, string targetLayerName,
        FeatureQuery query, string operationId, long maxBytes, CancellationToken cancellationToken)
    {
        if (JobSecurityScope.Current is { Submitter: null })
        {
            throw new UnauthorizedAccessException("Feature copy requires the submitting caller's security context.");
        }
        var snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(sourceLayerId, out var resource))
        {
            throw new InvalidOperationException("Source layer does not exist.");
        }
        var tenantId = JobSecurityScope.Current?.Submitter?.TenantId;
        var publication = snapshot.Graph.Publications.FirstOrDefault(publication =>
            publication.ResourceId == resource.Metadata.Id && snapshot.IsRoutable(publication)
            && snapshot.ResolveStorageLayerId(publication, resource) == sourceLayerId
            && snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var candidateService)
            && MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, candidateService, tenantId))
            ?? throw new UnauthorizedAccessException("Source layer is not accessible to the submitting tenant.");
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var serviceName = service.Metadata.Name;
        var reader = await router.ResolveReaderAsync(snapshot, service, resource, publication,
            sourceLayerId, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
        var source = reader as IStreamingFeatureStore
            ?? throw new InvalidOperationException("Source provider does not support streaming copy.");
        var srid = resource.Spatial?.SpatialReference?.ResolveSrid()
            ?? throw new InvalidOperationException("Source layer has no spatial reference.");
        var geometryField = resource.Spatial.PrimaryGeometryField ?? "geometry";
        var masked = fieldMasks is null ? [] : await fieldMasks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
        // Keep field references valid while representing redacted values as NULL.
        // The primary key is populated from the canonical feature identity below.
        var schemaFields = resource.SchemaFields.Select(f =>
            masked.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && !f.SemanticRoles.Contains("id.primary")
                ? f with { Nullable = true }
                : f).ToArray();
        var fields = schemaFields.Where(f => f.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)).ToArray();
        var primary = fields.Single(f => f.SemanticRoles.Contains("id.primary")).Name;
        var table = "gp_copy_" + Guid.NewGuid().ToString("N");
        await using var connectionOwner = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = connectionOwner.RequireNpgsqlConnection();
        await using var schemaCommand = new NpgsqlCommand("SELECT current_schema()", connection);
        var schema = (string)(await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        var qualified = SqlIdentifier.QuoteIdentifier(schema) + "." + SqlIdentifier.QuoteIdentifier(table);
        // A source with exact fields is copied as an exact layer; its DateTime fields then use a
        // zone-less column so the exact republish keeps them DateTime rather than TimestampOffset.
        var exactCopy = fields.Any(f => f.Type is MetadataV2FieldType.Decimal or MetadataV2FieldType.TimestampOffset);
        var columnDefinitions = fields.Select(f => SqlIdentifier.QuoteIdentifier(f.Name) + " "
            + (exactCopy && f.Type == MetadataV2FieldType.DateTime ? "timestamp without time zone" : SqlType(f))
            + (f.Nullable ? "" : " NOT NULL"));
        var ddl = $"CREATE TABLE {qualified} ({string.Join(",", columnDefinitions)}, {SqlIdentifier.QuoteIdentifier(geometryField)} geometry, "
            + $"PRIMARY KEY ({SqlIdentifier.QuoteIdentifier(primary)}), CHECK (ST_SRID({SqlIdentifier.QuoteIdentifier(geometryField)}) = {srid.ToString(CultureInfo.InvariantCulture)}))";
        long count = 0;
        long bytes = 0;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var create = new NpgsqlCommand(ddl + "; " + ImportTableOwnership.MarkSql(schema, table), connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var columns = string.Join(",", fields.Select(f => SqlIdentifier.QuoteIdentifier(f.Name)));
            var projected = string.Join(",", fields.Select(f => "r." + SqlIdentifier.QuoteIdentifier(f.Name)));
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {qualified} ({columns},{SqlIdentifier.QuoteIdentifier(geometryField)}) SELECT {projected}, ST_GeomFromWKB(@geometry,@srid) "
                + $"FROM jsonb_populate_record(NULL::{qualified}, @attributes) r", connection, transaction);
            insert.Parameters.Add("geometry", NpgsqlDbType.Bytea);
            insert.Parameters.AddWithValue("srid", srid);
            insert.Parameters.Add("attributes", NpgsqlDbType.Jsonb);
            // Selection, permanent filters, RLS, field masking and geometry ordinates
            // remain owned by the canonical reader. No source table SQL is duplicated.
            var copyQuery = query with { IncludeZ = true, IncludeM = true, OutputSrid = srid };
            await foreach (var feature in UnsupportedWhereSelection.Explain(
                source.StreamFeaturesAsync(sourceLayerId, copyQuery, cancellationToken),
                copyQuery.SqlFilter is null ? query.Where : null,
                cancellationToken).ConfigureAwait(false))
            {
                var attributes = feature.Attributes.ToDictionary(kv => kv.Key,
                    kv => kv.Value is byte[] binary ? (object)("\\x" + Convert.ToHexString(binary)) : kv.Value);
                if (exactCopy) StoreUtcWallClockDateTimes(attributes, fields);

                attributes.TryAdd(primary, feature.Id);
                var json = JsonSerializer.Serialize(attributes, FeatureAttributesJsonContext.Default.DictionaryStringObject);
                bytes += Encoding.UTF8.GetByteCount(json) + (feature.Geometry?.Length ?? 0);
                if (bytes > maxBytes)
                {
                    throw new InvalidOperationException("Feature copy exceeds the configured byte limit.");
                }
                insert.Parameters["geometry"].Value = (object?)feature.Geometry ?? DBNull.Value;
                insert.Parameters["attributes"].Value = json;
                count += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(connections.GetConnectionString())
        {
            SearchPath = schema + ",public"
        }.ConnectionString;
        // Keep the layer retired until its original schema, tenant and policies are
        // retained. A failed metadata copy cannot expose an unrestricted intermediate.
        try
        {
            var target = await publisher.PublishLayerAsync(connectionString, new LayerPublishRequest
            {
                AllowEmptyTable = true,
                Schema = schema,
                Table = table,
                LayerName = targetLayerName,
                GeometryColumn = geometryField,
                PrimaryKey = primary,
                Fields = fields.Select(f => f.Name).ToArray(),
                Srid = srid,
                ServiceName = serviceName,
                Enabled = false,
                ExactTypes = exactCopy
            }, cancellationToken).ConfigureAwait(false);
            var current = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var targetResource = current.Index.ResourcesByStorageLayerId[target.StorageLayerId];
            var annotations = resource.Metadata.Annotations.ToDictionary(kv => kv.Key, kv => kv.Value);
            annotations["gp.processId"] = "data-management.copy-features";
            annotations["gp.sourceLayerId"] = sourceLayerId.ToString(CultureInfo.InvariantCulture);
            annotations["gp.operationId"] = operationId;
            var copied = resource with
            {
                Metadata = resource.Metadata with
                {
                    Id = targetResource.Metadata.Id,
                    Name = targetLayerName,
                    Title = targetLayerName,
                    CreatedAt = targetResource.Metadata.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Annotations = annotations
                },
                StorageBindingIds = targetResource.StorageBindingIds,
                PrimaryStorageBindingId = targetResource.PrimaryStorageBindingId,
                SchemaFields = schemaFields,
                Spatial = resource.Spatial! with { Bbox = targetResource.Spatial?.Bbox, StorageCrs = resource.Spatial!.SpatialReference },
                Temporal = resource.Temporal is null ? null : resource.Temporal with { Extent = null },
                Relationships = [],
                Status = targetResource.Status
            };
            await metadata.SaveAsync(current.Graph with
            {
                Resources = current.Graph.Resources.Select(r => r.Metadata.Id == copied.Metadata.Id ? copied : r).ToArray()
            }, current.Etag, cancellationToken).ConfigureAwait(false);
            await publisher.SetLayerEnabledAsync(connectionString, target.StorageLayerId, serviceName, true, cancellationToken).ConfigureAwait(false);
            return new FeatureLayerCopyResult(target.StorageLayerId, count, srid);
        }
        catch
        {
            // The target name is generated by this invocation. Reconcile even when
            // publication committed but its response was lost, using an independent
            // bounded token so caller cancellation cannot prevent compensation.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CleanupAsync(connectionString, schema, table, cleanup.Token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupAsync(string connectionString, string schema, string table, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lookup = new NpgsqlCommand("SELECT layer_id FROM honua.layers WHERE table_schema = @schema AND table_name = @table", connection);
        lookup.Parameters.AddWithValue("schema", schema);
        lookup.Parameters.AddWithValue("table", table);
        var layerId = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (layerId is int id)
        {
            var current = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var resourceIds = current.Graph.StorageBindings.Where(b => b.StorageLayerId == id)
                .Select(b => b.ResourceId).ToHashSet(StringComparer.Ordinal);
            await metadata.SaveAsync(current.Graph with
            {
                Resources = current.Graph.Resources.Where(r => !resourceIds.Contains(r.Metadata.Id)).ToArray(),
                StorageBindings = current.Graph.StorageBindings.Where(b => !resourceIds.Contains(b.ResourceId)).ToArray(),
                Publications = current.Graph.Publications.Where(p => !resourceIds.Contains(p.ResourceId)).ToArray()
            }, current.Etag, cancellationToken).ConfigureAwait(false);
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var remove = new NpgsqlCommand("DELETE FROM honua.layers WHERE table_schema = @schema AND table_name = @table", connection, transaction);
        remove.Parameters.AddWithValue("schema", schema);
        remove.Parameters.AddWithValue("table", table);
        await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (await ImportTableOwnership.AssertOwnedAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            await using var drop = new NpgsqlCommand($"DROP TABLE {SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)}", connection, transaction);
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    // timestamp without time zone drops an offset using the session TimeZone.
    // Store the UTC wall clock so a DateTime field keeps its instant.
    private static void StoreUtcWallClockDateTimes(
        Dictionary<string, object?> attributes,
        MetadataV2Field[] fields)
    {
        foreach (var field in fields)
        {
            if (field.Type != MetadataV2FieldType.DateTime)
            {
                continue;
            }

            var key = attributes.Keys.FirstOrDefault(name =>
                name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
            if (key is not null && attributes[key] is { } temporal)
            {
                attributes[key] = ZoneLessUtcDateTime(temporal);
            }
        }
    }

    // A zoned instant becomes UTC wall-clock text. Text with no offset is already a civil time.
    internal static object ZoneLessUtcDateTime(object value)
    {
        const string format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff";
        switch (value)
        {
            case DateTimeOffset offset:
                return offset.UtcDateTime.ToString(format, CultureInfo.InvariantCulture);
            case DateTime dateTime:
                var utc = dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime;
                return utc.ToString(format, CultureInfo.InvariantCulture);
            case string text when HasExplicitOffset(text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed):
                return parsed.UtcDateTime.ToString(format, CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    private static bool HasExplicitOffset(string text)
        => text.EndsWith('Z') || text.EndsWith('z')
            || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');

    private static string SqlType(MetadataV2Field field) => field.Type switch
    {
        MetadataV2FieldType.String => field.Length is > 0 ? $"varchar({field.Length.Value.ToString(CultureInfo.InvariantCulture)})" : "text",
        MetadataV2FieldType.Integer => "integer",
        MetadataV2FieldType.BigInteger => "bigint",
        MetadataV2FieldType.Double => "double precision",
        MetadataV2FieldType.Decimal => "numeric",
        MetadataV2FieldType.Float => "real",
        MetadataV2FieldType.Boolean => "boolean",
        MetadataV2FieldType.DateTime or MetadataV2FieldType.TimestampOffset => "timestamp with time zone",
        MetadataV2FieldType.Date => "date",
        MetadataV2FieldType.Time => "time",
        MetadataV2FieldType.Json => "jsonb",
        MetadataV2FieldType.Binary => "bytea",
        MetadataV2FieldType.Uuid => "uuid",
        _ => throw new InvalidOperationException("Source field has no supported canonical type.")
    };
}
