// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Metadata v2 graph projection.
//
// Houses the logic that mirrors v1 honua.layers/services state into the canonical
// Metadata v2 graph (services, resources, storage bindings, publications, connections)
// and keeps refreshed layer extents in sync across both stores. Lives in its own file
// because the V2 builders + UpsertById/UpsertPublication helpers form a self-contained
// projection layer that is large, shared across the publish and extent-refresh paths,
// and easier to audit independently from the SQL-heavy persistence code.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// AOT-safe source-generated context for the primitive storage-binding option values.
/// The published server image sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>,
/// so the reflection-based <c>JsonSerializer.SerializeToElement(value)</c> overload throws at
/// runtime ("Reflection-based serialization has been disabled"). Building the option
/// <see cref="JsonElement"/>s through these typed metadata providers keeps the first layer
/// publish working on a real (trimmed/AOT) deployment (honua-server#1341).
/// </summary>
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class LayerPublishingStorageOptionJsonContext : JsonSerializerContext
{
}

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task UpsertPublishedLayerMetadataV2Async(
        string serviceName,
        LayerPublishRequest request,
        int layerId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        CancellationToken cancellationToken)
    {
        var (graph, expectedEtag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var service = BuildPublishedService(graph, serviceName, srid, now);
        var resource = BuildPublishedResource(
            request,
            layerId,
            primaryKeyColumn,
            geometryColumn,
            geometryType,
            srid,
            storageSrid,
            fields,
            extent,
            now);
        var binding = BuildPublishedStorageBinding(
            request,
            layerId,
            resource.Metadata.Id,
            schema,
            table,
            primaryKeyColumn,
            geometryColumn,
            storageSrid,
            now);
        var publication = BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            request.LayerName.Trim(),
            now);
        var connection = BuildPublishedConnection(request.ConnectionId, now);
        service = service with
        {
            PublicationIds = service.PublicationIds
                .Append(publication.Metadata.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Services = UpsertById(graph.Services, service, static item => item.Metadata.Id),
            Resources = UpsertById(graph.Resources, resource, static item => item.Metadata.Id),
            StorageBindings = UpsertById(graph.StorageBindings, binding, static item => item.Metadata.Id),
            Publications = UpsertPublication(graph.Publications, publication),
            Connections = connection is null
                ? graph.Connections
                : UpsertById(graph.Connections, connection, static item => item.Metadata.Id)
        };

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Published layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        await _metadataGraphStore.SaveAsync(updatedGraph, expectedEtag, cancellationToken).ConfigureAwait(false);
    }

    // Loads the active Metadata v2 graph for mutation, tolerating a fresh-DB
    // container where no snapshot has been activated yet (e.g. migration 031 ran
    // but the compat/bootstrap compile has not). In that case we start from an
    // empty graph and force the first write (null expectedEtag) instead of 500ing
    // the admin layer-publish path. (honua-server#1341.)
    private async Task<(MetadataV2Graph Graph, string? ExpectedEtag)> LoadCurrentOrEmptyGraphAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return (snapshot.Graph, snapshot.Etag);
        }
        catch (InvalidOperationException)
        {
            return (new MetadataV2Graph(), null);
        }
    }

    private async Task SyncRefreshedExtentsIntoV2GraphAsync(
        Dictionary<int, LayerExtentInsert?> refreshedExtents,
        CancellationToken cancellationToken)
    {
        if (refreshedExtents.Count == 0)
        {
            return;
        }

        var snapshot = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var graph = snapshot.Graph;

        // Map layer_id -> resource ids (a layer may be published into multiple services).
        var affectedResourceIds = new HashSet<string>(StringComparer.Ordinal);
        var extentByResourceId = new Dictionary<string, LayerExtentInsert?>(StringComparer.Ordinal);
        foreach (var publication in graph.Publications)
        {
            if (publication.LayerIndex is not int layerIndex) continue;
            if (!refreshedExtents.TryGetValue(layerIndex, out var extent)) continue;
            if (affectedResourceIds.Add(publication.ResourceId))
            {
                extentByResourceId[publication.ResourceId] = extent;
            }
        }
        if (affectedResourceIds.Count == 0)
        {
            return;
        }

        var updatedResources = graph.Resources
            .Select(resource =>
            {
                if (!affectedResourceIds.Contains(resource.Metadata.Id))
                {
                    return resource;
                }

                var extent = extentByResourceId[resource.Metadata.Id];
                MetadataV2Bbox? bbox = extent is null
                    ? null
                    : new MetadataV2Bbox
                    {
                        West = extent.MinX,
                        South = extent.MinY,
                        East = extent.MaxX,
                        North = extent.MaxY,
                    };

                var spatial = (resource.Spatial ?? new MetadataV2ResourceSpatial()) with { Bbox = bbox };
                return resource with { Spatial = spatial };
            })
            .ToArray();

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = updatedResources,
        };

        await _metadataGraphStore.SaveAsync(updatedGraph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static MetadataV2Service BuildPublishedService(
        MetadataV2Graph graph,
        string serviceName,
        int srid,
        DateTimeOffset now)
    {
        var existing = graph.Services.FirstOrDefault(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Metadata.Id, serviceName, StringComparison.Ordinal));

        var serviceId = existing?.Metadata.Id ?? $"svc-publish-{SanitizeMetadataId(serviceName)}";
        var metadata = (existing?.Metadata ?? new MetadataV2ObjectMetadata()) with
        {
            Id = serviceId,
            Name = string.IsNullOrWhiteSpace(existing?.Metadata.Name) ? serviceName : existing!.Metadata.Name,
            Title = existing?.Metadata.Title ?? serviceName,
            Description = existing?.Metadata.Description ?? $"Honua service '{serviceName}'",
            CreatedAt = existing?.Metadata.CreatedAt ?? now,
            UpdatedAt = now
        };

        return (existing ?? new MetadataV2Service()) with
        {
            Metadata = metadata,
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = $"/rest/services/{serviceName}/FeatureServer",
            Protocols = MetadataV2ServiceProtocols.All,
            SpatialReference = CreateSpatialReference(srid),
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Resource BuildPublishedResource(
        LayerPublishRequest request,
        int layerId,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        DateTimeOffset now)
    {
        var bindingId = BuildStorageBindingId(layerId);
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildResourceId(layerId),
                Name = request.LayerName.Trim(),
                Title = request.LayerName.Trim(),
                Description = request.Description,
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            SchemaFields = fields
                .Select(field => MapLayerFieldToMetadataV2(field, primaryKeyColumn, geometryColumn))
                .ToArray(),
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = CreateSpatialReference(srid),
                GeometryType = MapMetadataV2GeometryType(geometryType),
                Bbox = extent is not null && extent.Srid == srid
                    ? new MetadataV2Bbox
                    {
                        West = extent.MinX,
                        South = extent.MinY,
                        East = extent.MaxX,
                        North = extent.MaxY
                    }
                    : null,
                PrimaryGeometryField = geometryColumn,
                StorageCrs = CreateSpatialReference(storageSrid)
            },
            Display = new MetadataV2ResourceDisplay
            {
                DisplayField = fields
                    .FirstOrDefault(field =>
                        field.Type != MetadataV2FieldType.Geometry &&
                        !string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                    ?.Name,
                Queryable = true,
                DefaultVisibility = request.Enabled
            },
            Status = ActiveReadyStatus(now)
        };
    }

    private MetadataV2StorageBinding BuildPublishedStorageBinding(
        LayerPublishRequest request,
        int layerId,
        string resourceId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        int storageSrid,
        DateTimeOffset now)
    {
        var connectionId = request.ConnectionId?.ToString("D");
        var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [FeatureStorageMapping.SourceBackedOption] = BoolOption(true),
            ["schemaName"] = StringOption(schema),
            ["tableName"] = StringOption(table),
            ["primaryKeyColumn"] = StringOption(primaryKeyColumn),
            ["geometryColumn"] = StringOption(geometryColumn),
            ["storageSrid"] = IntOption(storageSrid)
        };

        // Layers published onto the shared Honua 'features' table store their non-key
        // attributes as keys inside the 'features.attributes' JSONB column (not as
        // physical columns) and share the table across layers via the 'layer_id'
        // discriminator column. Declare both so the storage-mapped reader projects
        // attributes->>'field' instead of bare columns (Postgres 42703) AND constrains
        // reads to this layer's rows (WHERE layer_id = StorageLayerId) — without the
        // discriminator a query for layer A would return layer B's features.
        //
        // Gate on the ACTUAL shared table: name == 'features' AND schema == the Honua
        // metadata schema. A user source table that merely happens to be named
        // 'features' in another schema (e.g. public.features) has neither the JSONB
        // 'attributes' column nor 'layer_id', so applying these options there would make
        // the reader emit columns the table lacks and fail with 42703. (honua-server#1238.)
        if (string.Equals(table, DatabaseSchema.FeaturesTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(schema, _metadataSchema, StringComparison.OrdinalIgnoreCase))
        {
            options["attributesColumn"] = StringOption("attributes");
            options["layerDiscriminatorColumn"] = StringOption(DatabaseSchema.LayerIdColumn);
        }

        return new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildStorageBindingId(layerId),
                Name = BuildStorageBindingId(layerId),
                Title = $"{schema}.{table}",
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resourceId,
            ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{schema}.{table}",
            StorageLayerId = layerId,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate
            ],
            Options = options,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Publication BuildPublishedPublication(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2StorageBinding binding,
        string layerIdText,
        string layerTitle,
        DateTimeOffset now)
    {
        return new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"pub-{service.Metadata.Id}-{layerIdText}",
                Name = layerIdText,
                Title = layerTitle,
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resource.Metadata.Id,
            ServiceId = service.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIdText,
                IsNumeric = true
            },
            IsPrimary = true,
            SupportedFormats = _defaultFormats,
            Capabilities = _defaultCapabilities,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Connection? BuildPublishedConnection(Guid? connectionId, DateTimeOffset now)
    {
        if (!connectionId.HasValue)
        {
            return null;
        }

        var id = connectionId.Value.ToString("D");
        return new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = id,
                Title = "PostGIS secure connection",
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ConnectionType.Database,
            Provider = DataProviderNames.Postgis,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Field MapLayerFieldToMetadataV2(
        LayerFieldInsert field,
        string primaryKeyColumn,
        string geometryColumn)
    {
        var semanticRoles = new List<string>(capacity: 2);
        if (string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("id.primary");
        }
        if (string.Equals(field.Name, geometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("geometry.primary");
        }

        return new MetadataV2Field
        {
            Name = field.Name,
            Type = MapMetadataV2FieldType(field.Type),
            Title = field.Name,
            Description = field.Description,
            Nullable = field.Nullable,
            SemanticRoles = semanticRoles.ToArray(),
            Alias = field.Name,
            Editable = field.Type != MetadataV2FieldType.Geometry,
            Length = field.MaxLength,
            // Carry the captured Esri coded-value/range domain into the canonical
            // graph so it survives the compat-compile snapshot and is served via the
            // FeatureServer field domain and queryDomains surfaces (honua-server#1255).
            Domain = field.Domain
        };
    }

    private static MetadataV2FieldType MapMetadataV2FieldType(MetadataV2FieldType fieldType)
        => fieldType switch
        {
            MetadataV2FieldType.String => MetadataV2FieldType.String,
            MetadataV2FieldType.Integer => MetadataV2FieldType.Integer,
            MetadataV2FieldType.BigInteger => MetadataV2FieldType.BigInteger,
            MetadataV2FieldType.Double => MetadataV2FieldType.Double,
            MetadataV2FieldType.Float => MetadataV2FieldType.Float,
            MetadataV2FieldType.Boolean => MetadataV2FieldType.Boolean,
            MetadataV2FieldType.DateTime => MetadataV2FieldType.DateTime,
            MetadataV2FieldType.Date => MetadataV2FieldType.Date,
            MetadataV2FieldType.Time => MetadataV2FieldType.Time,
            MetadataV2FieldType.Geometry => MetadataV2FieldType.Geometry,
            MetadataV2FieldType.Json => MetadataV2FieldType.Json,
            MetadataV2FieldType.Binary => MetadataV2FieldType.Binary,
            MetadataV2FieldType.Uuid => MetadataV2FieldType.Uuid,
            _ => MetadataV2FieldType.Unknown
        };

    private static MetadataV2GeometryType MapMetadataV2GeometryType(string geometryType)
        => geometryType.Trim().ToLowerInvariant() switch
        {
            "point" => MetadataV2GeometryType.Point,
            "multipoint" => MetadataV2GeometryType.MultiPoint,
            "linestring" => MetadataV2GeometryType.LineString,
            "multilinestring" => MetadataV2GeometryType.MultiLineString,
            "polygon" => MetadataV2GeometryType.Polygon,
            "multipolygon" => MetadataV2GeometryType.MultiPolygon,
            "geometrycollection" => MetadataV2GeometryType.GeometryCollection,
            _ => MetadataV2GeometryType.Mixed
        };

    private static MetadataV2SpatialReference CreateSpatialReference(int srid)
        => new()
        {
            Srid = srid,
            Crs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}",
            IsGeographic = srid == 4326
        };

    private static MetadataV2Status ActiveReadyStatus(DateTimeOffset now)
        => new()
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
            ObservedAt = now
        };

    private static List<T> UpsertById<T>(
        IReadOnlyList<T> items,
        T item,
        Func<T, string> idSelector)
    {
        var itemId = idSelector(item);
        var result = new List<T>(items.Count + 1);
        var replaced = false;

        foreach (var existing in items)
        {
            if (string.Equals(idSelector(existing), itemId, StringComparison.Ordinal))
            {
                if (!replaced)
                {
                    result.Add(item);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(item);
        }

        return result;
    }

    private static List<MetadataV2Publication> UpsertPublication(
        IReadOnlyList<MetadataV2Publication> publications,
        MetadataV2Publication publication)
    {
        var result = new List<MetadataV2Publication>(publications.Count + 1);
        var replaced = false;

        foreach (var existing in publications)
        {
            var sameIdentity = string.Equals(
                existing.Metadata.Id,
                publication.Metadata.Id,
                StringComparison.Ordinal);
            var sameServiceLayer = string.Equals(
                existing.ServiceId,
                publication.ServiceId,
                StringComparison.Ordinal) &&
                existing.LayerIndex == publication.LayerIndex;

            if (sameIdentity || sameServiceLayer)
            {
                if (!replaced)
                {
                    result.Add(publication);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(publication);
        }

        return result;
    }

    private static string BuildResourceId(int layerId)
        => $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildStorageBindingId(int layerId)
        => $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static JsonElement BoolOption(bool value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.Boolean);

    private static JsonElement IntOption(int value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.Int32);

    private static JsonElement StringOption(string value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.String);

    private static string SanitizeMetadataId(string value)
    {
        var trimmed = value.Trim();
        var chars = trimmed.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? ch
                : '-');
        var sanitized = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}
