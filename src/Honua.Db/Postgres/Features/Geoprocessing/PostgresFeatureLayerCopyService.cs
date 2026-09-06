// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Geoprocessing;

/// <summary>Materializes a canonical filtered stream and publishes an independent typed layer.</summary>
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
        var publication = snapshot.Graph.Publications.First(p => p.ResourceId == resource.Metadata.Id && snapshot.IsRoutable(p));
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
        var schemaFields = resource.SchemaFields.Where(f => !masked.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var fields = schemaFields.Where(f => f.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)).ToArray();
        var primary = fields.Single(f => f.SemanticRoles.Contains("id.primary")).Name;
        var table = "gp_copy_" + Guid.NewGuid().ToString("N");
        await using var connection = (NpgsqlConnection)await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var schemaCommand = new NpgsqlCommand("SELECT current_schema()", connection);
        var schema = (string)(await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        var qualified = Quote(schema) + "." + Quote(table);
        var columnDefinitions = fields.Select(f => Quote(f.Name) + " " + SqlType(f) + (f.Nullable ? "" : " NOT NULL"));
        var ddl = $"CREATE TABLE {qualified} ({string.Join(",", columnDefinitions)}, {Quote(geometryField)} geometry, "
            + $"PRIMARY KEY ({Quote(primary)}), CHECK (ST_SRID({Quote(geometryField)}) = {srid.ToString(CultureInfo.InvariantCulture)}))";
        long count = 0;
        long bytes = 0;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var create = new NpgsqlCommand(ddl, connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var columns = string.Join(",", fields.Select(f => Quote(f.Name)));
            var projected = string.Join(",", fields.Select(f => "r." + Quote(f.Name)));
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {qualified} ({columns},{Quote(geometryField)}) SELECT {projected}, ST_GeomFromWKB(@geometry,@srid) "
                + $"FROM jsonb_populate_record(NULL::{qualified}, @attributes) r", connection, transaction);
            insert.Parameters.Add("geometry", NpgsqlDbType.Bytea);
            insert.Parameters.AddWithValue("srid", srid);
            insert.Parameters.Add("attributes", NpgsqlDbType.Jsonb);
            // Selection, permanent filters, RLS, field masking and geometry ordinates
            // remain owned by the canonical reader. No source table SQL is duplicated.
            await foreach (var feature in source.StreamFeaturesAsync(sourceLayerId,
                query with { IncludeZ = true, IncludeM = true, OutputSrid = srid }, cancellationToken).ConfigureAwait(false))
            {
                var attributes = feature.Attributes.ToDictionary(kv => kv.Key,
                    kv => kv.Value is byte[] binary ? (object)("\\x" + Convert.ToHexString(binary)) : kv.Value);
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
            Schema = schema,
            Table = table,
            LayerName = targetLayerName,
            GeometryColumn = geometryField,
            PrimaryKey = primary,
            Fields = fields.Select(f => f.Name).ToArray(),
            Srid = srid,
            ServiceName = serviceName,
            Enabled = false
        }, cancellationToken).ConfigureAwait(false);
        var current = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var targetResource = current.Index.ResourcesByStorageLayerId[target.LayerId];
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
        await publisher.SetLayerEnabledAsync(connectionString, target.LayerId, serviceName, true, cancellationToken).ConfigureAwait(false);
        return new FeatureLayerCopyResult(target.LayerId, count, srid);
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
        await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {Quote(schema)}.{Quote(table)}", connection, transaction);
        await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string SqlType(MetadataV2Field field) => field.Type switch
    {
        MetadataV2FieldType.String => field.Length is > 0 ? $"varchar({field.Length.Value.ToString(CultureInfo.InvariantCulture)})" : "text",
        MetadataV2FieldType.Integer => "integer",
        MetadataV2FieldType.BigInteger => "bigint",
        MetadataV2FieldType.Double => "double precision",
        MetadataV2FieldType.Float => "real",
        MetadataV2FieldType.Boolean => "boolean",
        MetadataV2FieldType.DateTime => "timestamp with time zone",
        MetadataV2FieldType.Date => "date",
        MetadataV2FieldType.Time => "time",
        MetadataV2FieldType.Json => "jsonb",
        MetadataV2FieldType.Binary => "bytea",
        MetadataV2FieldType.Uuid => "uuid",
        _ => throw new InvalidOperationException("Source field has no supported canonical type.")
    };
}
