// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// V2 overloads of the response-builder helpers on
/// <see cref="FeatureServerEndpoints"/> (issue #1035, FeatureServer cutover 91/N).
/// These build GeoServices metadata directly from Metadata V2 graph objects:
/// <list type="bullet">
///   <item><c>layer.Fields</c>            → <c>resource.SchemaFields</c></item>
///   <item><c>layer.AttributeFields</c>   → <c>resource.SchemaFields.Where(f => f.Type is not (Geometry or Geography))</c></item>
///   <item><c>layer.Name</c>              → <c>resource.Metadata.Name</c></item>
///   <item><c>layer.Id</c>                → <c>publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1</c></item>
///   <item><c>layer.GeometryType</c>      → <c>resource.Spatial?.GeometryType</c></item>
///   <item><c>layer.SpatialReference.Srid</c> → <c>resource.ReadSrid()</c></item>
///   <item><c>service.Layers</c>          → iterate <c>snapshot.Index.PublicationsByService[serviceId]</c></item>
///   <item><c>service.SpatialReference</c> → <c>service.SpatialReference?.ResolveSrid()</c></item>
/// </list>
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Builds a GeoServices FeatureServer response from a Metadata V2 service and its publications.
    /// </summary>
    /// <param name="service">V2 service backing the FeatureServer route.</param>
    /// <param name="publications">Resolved (publication, resource) tuples for the service in
    /// service-local-id order. Callers typically materialize this from
    /// <c>snapshot.PublicationsForService(serviceId)</c> + <c>snapshot.ResolveResource(pub)</c>.</param>
    /// <param name="snapshot">Graph snapshot used to resolve storage layer ids.</param>
    /// <param name="queryLimits">Query limits for the service.</param>
    /// <param name="supportsGeobufOutput">Whether the runtime supports geobuf output.</param>
    /// <param name="supportsAttachmentUploads">Whether attachment uploads are wired up.</param>
    private static FeatureServerResponse MapServiceToResponseV2(
        MetadataV2Service service,
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        MetadataV2GraphSnapshot snapshot,
        QueryLimits queryLimits,
        bool supportsGeobufOutput,
        bool supportsAttachmentUploads)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(snapshot);

        var objectIdField = ResolveServiceObjectIdFieldV2(service, snapshot);
        var supportsStatistics = true;
        var supportsAdvancedQueries = ServiceSupportsAdvancedQueriesV2(service);
        var hasGeometry = publications.Any(pair =>
            pair.Resource.Spatial is { GeometryType: not MetadataV2GeometryType.None });

        var visibleFields = publications
            .SelectMany(pair => ResolveVisibleFieldsV2(pair.Resource))
            .Select(field => MapFieldInfoV2(field, objectIdField))
            .ToArray();

        var supportedFormats = ReadServiceSupportedFormatsV2(service);
        var supportsEditing = ServiceSupportsEditingV2(service);
        var srid = service.SpatialReference?.ResolveSrid();
        var spatialReference = srid.HasValue
            ? SpatialReference.Create(srid.Value).ToSpatialReferenceInfo()
            : SpatialReference.WGS84.ToSpatialReferenceInfo();
        var serviceExtent = ResolveServiceExtentV2(publications, srid ?? SpatialReference.WGS84.Wkid);

        return new FeatureServerResponse
        {
            ServiceName = service.Metadata.Name,
            ServiceDescription = service.Metadata.Description ?? string.Empty,
            Layers = [.. publications.Select(pair => MapLayerInfoV2(pair.Resource, pair.Publication, snapshot))],
            SpatialReference = spatialReference,
            InitialExtent = serviceExtent,
            FullExtent = serviceExtent,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = NormalizeSupportedQueryFormats(supportedFormats, supportsGeobufOutput),
            Capabilities = BuildServiceCapabilitiesV2(service, publications, supportsAttachmentUploads),
            Fields = visibleFields,
            ObjectIdField = objectIdField,
            SupportsAdvancedQueries = supportsAdvancedQueries,
            SupportsStatistics = supportsStatistics,
            HasGeometryProperties = hasGeometry,
            AllowGeometryUpdates = supportsEditing,
            SyncEnabled = ServiceSupportsSyncV2(service)
        };
    }

    /// <summary>
    /// Builds a GeoServices layer response from a Metadata V2 resource publication.
    /// </summary>
    private static LayerResponse MapLayerToResponseV2(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2GraphSnapshot snapshot,
        QueryLimits queryLimits,
        FeatureServerTimeInfo? timeInfo,
        JsonElement? drawingInfo,
        FeatureServerExtrusionInfo? extrusionInfo,
        bool supportsGeobufOutput,
        bool supportsAttachmentUploads)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(snapshot);

        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var displayField = ResolveDisplayFieldFromResource(resource, objectIdField);
        var supportsStatistics = true;
        var supportsAdvancedQueries = ServiceSupportsAdvancedQueriesV2(service);
        var supportsRelated = resource.Relationships.Count > 0;
        var supportsOrderBy = supportsAdvancedQueries;
        var supportsDistinct = supportsAdvancedQueries;
        var supportsPagination = supportsAdvancedQueries;
        var supportsEditing = ServiceSupportsEditingV2(service);
        var advancedQueryCapabilities = BuildAdvancedQueryCapabilities(
            supportsAdvancedQueries,
            supportsStatistics,
            supportsOrderBy,
            supportsDistinct,
            supportsPagination);

        var srid = resource.ReadSrid();
        var spatialReference = srid.HasValue
            ? SpatialReference.Create(srid.Value).ToSpatialReferenceInfo()
            : SpatialReference.WGS84.ToSpatialReferenceInfo();

        var supportedFormats = ReadServiceSupportedFormatsV2(service);
        var supportsAttachments = ResourceSupportsAttachmentsV2(resource);
        var extent = ResolveLayerExtentV2(resource, srid ?? SpatialReference.WGS84.Wkid);

        return new LayerResponse
        {
            Id = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1,
            Name = resource.Metadata.Name,
            Description = resource.Metadata.Description,
            Type = "Feature Layer",
            GeometryType = MapGeometryTypeV2(resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None),
            SpatialReference = spatialReference,
            Extent = extent,
            TimeInfo = timeInfo,
            ExtrusionInfo = extrusionInfo,
            Fields = [.. ResolveVisibleFieldsV2(resource).Select(field => MapFieldInfoV2(field, objectIdField))],
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField,
            DisplayField = displayField,
            UniqueIdField = new UniqueIdFieldInfo { Name = objectIdField, IsSystemMaintained = true },
            DrawingInfo = drawingInfo.HasValue ? drawingInfo.Value : null,
            Capabilities = BuildLayerCapabilitiesV2(service, resource, supportsAttachmentUploads),
            SupportsAdvancedQueries = supportsAdvancedQueries,
            SupportsStatistics = supportsStatistics,
            SupportsCountDistinct = supportsStatistics,
            SupportsOrderBy = supportsOrderBy,
            SupportsDistinct = supportsDistinct,
            SupportsPagination = supportsPagination,
            SupportsTrueCurve = false,
            SupportsReturningQueryExtent = supportsAdvancedQueries,
            SupportsRollbackOnFailureParameter = supportsEditing,
            SupportsApplyEditsWithGlobalIds = false,
            HasAttachments = supportsAttachments && supportsAttachmentUploads,
            SupportsQueryRelated = supportsRelated,
            SupportedQueryFormats = NormalizeSupportedQueryFormats(supportedFormats, supportsGeobufOutput),
            SupportsCoordinatesQuantization = false,
            Relationships = BuildRelationshipResponseV2(resource, snapshot),
            AllowGeometryUpdates = supportsEditing,
            EditFieldsInfo = null,
            EditingInfo = supportsEditing ? new EditingInfo() : null,
            Templates = [],
            AdvancedQueryCapabilities = advancedQueryCapabilities
        };
    }

    /// <summary>
    /// Builds the lightweight service-listing entry (id/name/geometry-type) for a
    /// Metadata V2 resource publication.
    /// </summary>
    private static LayerInfo MapLayerInfoV2(
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new LayerInfo
        {
            Id = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1,
            Name = resource.Metadata.Name,
            // V2 has no default-visibility / min-scale / max-scale slot on the canonical
            // resource. Esri clients require the keys present, so we emit the conservative
            // defaults that match what the v1 path emitted for layers without scale ranges.
            DefaultVisibility = true,
            SubLayerIds = null,
            MinScale = null,
            MaxScale = null,
            GeometryType = MapGeometryTypeV2(resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None)
        };
    }

    /// <summary>
    /// Maps a canonical <see cref="MetadataV2Field"/> to the Esri-style GeoServices
    /// field info shape. The layer's object-id field is forced to
    /// <c>esriFieldTypeOID</c> regardless of its SQL type so that Esri clients can
    /// locate the OID field by type (see issue #1299).
    /// </summary>
    private static GeoServicesFieldInfo MapFieldInfoV2(MetadataV2Field field, string objectIdFieldName)
    {
        ArgumentNullException.ThrowIfNull(field);

        var isObjectId = field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
        var geoServicesType = isObjectId ? "esriFieldTypeOID" : MapFieldTypeToGeoServicesV2(field.Type);
        var sqlType = MapFieldTypeToSqlV2(field.Type);
        var isGeometry = field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

        return new GeoServicesFieldInfo
        {
            Name = field.Name,
            Type = geoServicesType,
            SqlType = sqlType,
            Alias = field.Alias ?? field.Title ?? field.Name,
            // V2 has no length slot on the canonical field model (length lives in the
            // storage binding when needed). Pass null so the response omits the key.
            Length = null,
            Nullable = field.Nullable && !isObjectId,
            Editable = !isGeometry && !isObjectId,
            // V2 has no default-value slot on the canonical field; the catalog/admin layer
            // owns insertion defaults.
            DefaultValue = null,
            Domain = GeoServicesFieldDomainMapper.Map(field.Domain),
            Visible = !field.Hidden
        };
    }

    /// <summary>
    /// Reads capabilities from the service's <c>Options["capabilities"]</c> JsonElement when
    /// present; otherwise defaults to <c>"Query"</c>. Emits <c>Create/Update/Delete/Editing</c>
    /// when any of those edit-capability tokens are present, and <c>Uploads</c> when any
    /// publication's backing resource declares attachment support and the runtime has
    /// attachment uploads enabled.
    /// </summary>
    private static string BuildServiceCapabilitiesV2(
        MetadataV2Service service,
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        bool supportsAttachmentUploads)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(publications);

        var capabilities = new List<string>();
        var declared = ReadServiceCapabilitiesV2(service);

        if (declared.Any(capability => capability.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
        }

        if (ServiceSupportsEditingV2(service))
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        if (declared.Any(capability => capability.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Extract");
        }

        // Advertise "Sync" when the service declares it (syncEnabled). The
        // createReplica / synchronizeReplica endpoints are served, but Esri SDK
        // clients only attempt the sync surface when the capabilities string
        // includes "Sync"; omitting it left a working sync backend unreachable.
        if (ServiceSupportsSyncV2(service))
        {
            capabilities.Add("Sync");
        }

        if (supportsAttachmentUploads && publications.Any(pair => ResourceSupportsAttachmentsV2(pair.Resource)))
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the GeoServices capabilities string for one Metadata V2 resource.
    /// </summary>
    private static string BuildLayerCapabilitiesV2(
        MetadataV2Service service,
        MetadataV2Resource resource,
        bool supportsAttachmentUploads)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resource);

        var capabilities = new List<string>();
        var declared = ReadServiceCapabilitiesV2(service);

        if (declared.Any(capability => capability.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
        }

        if (ServiceSupportsEditingV2(service))
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        if (declared.Any(capability => capability.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Extract");
        }

        // Advertise "Sync" when the service declares it (syncEnabled). The
        // createReplica / synchronizeReplica endpoints are served, but Esri SDK
        // clients only attempt the sync surface when the capabilities string
        // includes "Sync"; omitting it left a working sync backend unreachable.
        if (ServiceSupportsSyncV2(service))
        {
            capabilities.Add("Sync");
        }

        if (supportsAttachmentUploads && ResourceSupportsAttachmentsV2(resource))
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Aggregates the per-resource object-id field across all publications on the service
    /// and returns the single shared name when all resources agree; otherwise falls back to
    /// the default <see cref="FieldNames.ObjectId"/> constant.
    /// </summary>
    private static string ResolveServiceObjectIdFieldV2(
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidates = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Select(pub => snapshot.ResolveResource(pub))
            .Where(resource => resource is not null)
            .Select(resource => GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource!))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : FieldNames.ObjectId;
    }

    /// <summary>
    /// Builds a GeoServices extent from the v2 resource bbox.
    /// </summary>
    private static ExtentInfo? ResolveLayerExtentV2(MetadataV2Resource resource, int fallbackSrid)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var bbox = resource.ReadBbox();
        if (bbox is null)
        {
            return null;
        }

        var srid = resource.ReadSrid() ?? fallbackSrid;
        return FeatureExtent.Create(bbox.West, bbox.South, bbox.East, bbox.North, srid).ToExtentInfo();
    }

    /// <summary>
    /// Builds the service extent by unioning v2 resource bboxes.
    /// </summary>
    private static ExtentInfo? ResolveServiceExtentV2(
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        int fallbackSrid)
    {
        double? west = null;
        double? south = null;
        double? east = null;
        double? north = null;

        foreach (var (_, resource) in publications)
        {
            var bbox = resource.ReadBbox();
            if (bbox is null)
            {
                continue;
            }

            west = west.HasValue ? Math.Min(west.Value, bbox.West) : bbox.West;
            south = south.HasValue ? Math.Min(south.Value, bbox.South) : bbox.South;
            east = east.HasValue ? Math.Max(east.Value, bbox.East) : bbox.East;
            north = north.HasValue ? Math.Max(north.Value, bbox.North) : bbox.North;
        }

        return west.HasValue && south.HasValue && east.HasValue && north.HasValue
            ? FeatureExtent.Create(west.Value, south.Value, east.Value, north.Value, fallbackSrid).ToExtentInfo()
            : null;
    }

    /// <summary>
    /// Resolves the FeatureServer display field. Prefers <c>resource.Display.DisplayField</c>
    /// when it matches a visible attribute (matching MapServer/KML behaviour), then a field
    /// named "name", then the first string-type field, then <paramref name="objectIdField"/>.
    /// </summary>
    private static string ResolveDisplayFieldFromResource(MetadataV2Resource resource, string objectIdField)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var visibleAttributes = ResolveVisibleAttributeFieldsV2(resource).ToArray();

        var configuredDisplayField = resource.Display?.DisplayField;
        if (!string.IsNullOrWhiteSpace(configuredDisplayField))
        {
            var configured = visibleAttributes.FirstOrDefault(
                field => field.Name.Equals(configuredDisplayField, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
            {
                return configured.Name;
            }
        }

        var preferredNameField = visibleAttributes.FirstOrDefault(
            field => field.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (preferredNameField is not null)
        {
            return preferredNameField.Name;
        }

        var firstStringField = visibleAttributes.FirstOrDefault(
            field => field.Type == MetadataV2FieldType.String);
        return firstStringField?.Name ?? objectIdField;
    }

    /// <summary>
    /// V2 overload of <c>layer.VisibleFields</c>.
    /// </summary>
    private static IEnumerable<MetadataV2Field> ResolveVisibleFieldsV2(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Where(static field => !field.Hidden);
    }

    /// <summary>
    /// V2 overload of <c>layer.VisibleAttributeFields</c>. Excludes geometry/geography fields.
    /// </summary>
    private static IEnumerable<MetadataV2Field> ResolveVisibleAttributeFieldsV2(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Where(field =>
            !field.Hidden &&
            field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography));
    }

    /// <summary>
    /// Reads the temporal field configuration from <see cref="MetadataV2Resource.Temporal"/>
    /// and probes the feature store for the range. Returns null when the resource does not
    /// declare opt-in temporal fields.
    /// </summary>
    private static async Task<FeatureServerTimeInfo?> BuildTimeInfoV2Async(
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2GraphSnapshot snapshot,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(featureReader);

        if (!TemporalExtentHelpers.HasOptInTemporalFields(resource))
        {
            return null;
        }

        var storageLayerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(resource)
            ?? -1;
        if (storageLayerId < 0)
        {
            return null;
        }

        var temporalRange = await TemporalExtentHelpers.TryResolveTemporalRangeV2Async(
            resource,
            storageLayerId,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (temporalRange is null)
        {
            return null;
        }

        var range = temporalRange.Value;
        long? minMs = range.Min?.ToUnixTimeMilliseconds();
        long? maxMs = range.Max?.ToUnixTimeMilliseconds();

        return new FeatureServerTimeInfo
        {
            StartTimeField = range.StartFieldName,
            EndTimeField = range.EndFieldName,
            TrackIdField = resource.Temporal?.TrackIdField,
            TimeExtent = new long?[] { minMs, maxMs }
        };
    }

    /// <summary>
    /// Parses <c>layerId</c>/<c>layers</c> query parameters and returns the matching
    /// (publication, resource) tuples on the service. <c>selectorSpecified</c> is true when
    /// either parameter was supplied.
    /// </summary>
    internal static bool TryResolveRequestedServiceLayersV2(
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot,
        IReadOnlyDictionary<string, StringValues> values,
        out (MetadataV2Publication Publication, MetadataV2Resource Resource)[] layers,
        out bool selectorSpecified,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Materialize the full publication list once so we can both default-return and filter
        // against it with stable layer-id resolution.
        var allPairs = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Select(pub => (Publication: pub, Resource: snapshot.ResolveResource(pub)))
            .Where(pair => pair.Resource is not null)
            .Select(pair => (pair.Publication, Resource: pair.Resource!))
            .ToArray();

        layers = allPairs;
        selectorSpecified = false;
        error = null;

        HashSet<int>? requestedLayerIds = null;

        if (TryGetValue(values, "layerId", out var layerIdRaw) && !StringValues.IsNullOrEmpty(layerIdRaw))
        {
            if (!int.TryParse(layerIdRaw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                error = "layerId must be an integer.";
                return false;
            }

            requestedLayerIds ??= [];
            requestedLayerIds.Add(layerId);
        }

        if (TryGetValue(values, "layers", out var layersRaw) && !StringValues.IsNullOrEmpty(layersRaw))
        {
            if (!TryParseLayerIdList(layersRaw.ToString(), out var parsedLayerIds, out error))
            {
                return false;
            }

            requestedLayerIds ??= [];
            foreach (var layerId in parsedLayerIds)
            {
                requestedLayerIds.Add(layerId);
            }
        }

        if (requestedLayerIds is null or { Count: 0 })
        {
            return true;
        }

        selectorSpecified = true;
        layers =
        [
            ..allPairs.Where(pair =>
            {
                var id = pair.Publication.LayerIndex ?? snapshot.ResolveStorageLayerId(pair.Resource) ?? -1;
                return requestedLayerIds.Contains(id);
            })
        ];
        if (layers.Length != requestedLayerIds.Count)
        {
            error = "layers must reference valid layer identifiers in the service.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Drops (publication, resource) pairs the caller is not authorised to read using the V2
    /// access-policy resolver (resource policy composed with service policy under deny-wins).
    /// </summary>
    internal static (MetadataV2Publication Publication, MetadataV2Resource Resource)[] FilterAccessibleLayersV2(
        HttpContext context,
        MetadataV2Service service,
        IEnumerable<(MetadataV2Publication Publication, MetadataV2Resource Resource)> layers)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(layers);

        return
        [
            ..layers.Where(pair => AccessPolicyHelpers.IsResourceAccessible(context, pair.Resource, service))
        ];
    }

    /// <summary>
    /// Projects <see cref="MetadataV2Resource.Relationships"/> to the Esri-style wire shape.
    /// The integer <c>RelatedTableId</c> is resolved by looking up the related resource's
    /// primary publication and reading its <see cref="MetadataV2Publication.LayerIndex"/>; if
    /// no publication exists on this service for the related resource, the relationship is
    /// skipped (clients can't address it as an integer layer id anyway).
    /// </summary>
    private static LayerRelationshipInfo[] BuildRelationshipResponseV2(
        MetadataV2Resource resource,
        MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (resource.Relationships.Count == 0)
        {
            return [];
        }

        var result = new List<LayerRelationshipInfo>(resource.Relationships.Count);
        foreach (var relationship in resource.Relationships)
        {
            // Esri clients expect a numeric relationship id; prefer the explicit Esri id on
            // the V2 relationship, else derive a stable integer from the relationship's
            // string id hash so it round-trips across requests.
            var relationshipId = relationship.EsriRelationshipId
                ?? StableStringHash(relationship.Id);

            var relatedResource = snapshot.Index.ResourcesById.TryGetValue(relationship.RelatedResourceId, out var found)
                ? found
                : null;
            var relatedLayerId = relatedResource is null
                ? -1
                : (snapshot.Index.PublicationsByResource[relatedResource.Metadata.Id]
                       .Select(pub => (int?)pub.LayerIndex)
                       .FirstOrDefault(idx => idx is not null)
                   ?? snapshot.ResolveStorageLayerId(relatedResource)
                   ?? -1);

            result.Add(new LayerRelationshipInfo
            {
                Id = relationshipId,
                Name = relationship.Name,
                RelatedTableId = relatedLayerId,
                Role = relationship.Role,
                KeyField = relationship.DestinationField,
                OriginKeyField = relationship.OriginField,
                DestinationKeyField = relationship.DestinationField,
                Description = relationship.Description
            });
        }

        return [.. result];
    }

    internal static int StableStringHash(string value)
    {
        // FNV-1a 32-bit hash, then clamped to a non-negative int so the Esri client doesn't
        // refuse a "negative" relationship id.
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }
        return (int)(hash & 0x7FFFFFFFu);
    }

    private static string MapGeometryTypeV2(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.LineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon => "esriGeometryPolygon",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            MetadataV2GeometryType.GeometryCollection => "esriGeometryNull",
            MetadataV2GeometryType.Mixed => "esriGeometryNull",
            MetadataV2GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryNull"
        };

    private static string MapFieldTypeToGeoServicesV2(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "esriFieldTypeString",
            MetadataV2FieldType.Integer => "esriFieldTypeInteger",
            MetadataV2FieldType.BigInteger => "esriFieldTypeInteger64",
            MetadataV2FieldType.Double => "esriFieldTypeDouble",
            MetadataV2FieldType.Float => "esriFieldTypeSingle",
            MetadataV2FieldType.Boolean => "esriFieldTypeSmallInteger",
            MetadataV2FieldType.DateTime => "esriFieldTypeDate",
            MetadataV2FieldType.Date => "esriFieldTypeDate",
            MetadataV2FieldType.Time => "esriFieldTypeString",
            MetadataV2FieldType.Json => "esriFieldTypeString",
            MetadataV2FieldType.Binary => "esriFieldTypeBlob",
            MetadataV2FieldType.Uuid => "esriFieldTypeGUID",
            MetadataV2FieldType.Geometry => "esriFieldTypeGeometry",
            MetadataV2FieldType.Geography => "esriFieldTypeGeometry",
            _ => "esriFieldTypeString"
        };

    private static string MapFieldTypeToSqlV2(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "TEXT",
            MetadataV2FieldType.Integer => "INTEGER",
            MetadataV2FieldType.BigInteger => "BIGINT",
            MetadataV2FieldType.Double => "DOUBLE PRECISION",
            MetadataV2FieldType.Float => "REAL",
            MetadataV2FieldType.Boolean => "BOOLEAN",
            MetadataV2FieldType.DateTime => "TIMESTAMP WITH TIME ZONE",
            MetadataV2FieldType.Date => "DATE",
            MetadataV2FieldType.Time => "TIME",
            MetadataV2FieldType.Json => "JSONB",
            MetadataV2FieldType.Binary => "BYTEA",
            MetadataV2FieldType.Uuid => "UUID",
            MetadataV2FieldType.Geometry => "GEOMETRY",
            MetadataV2FieldType.Geography => "GEOGRAPHY",
            _ => "TEXT"
        };

    /// <summary>
    /// Reads the service's declared capability list from <c>service.Options["capabilities"]</c>.
    /// Defaults to <c>["Query"]</c> when the option is absent or not a string array.
    /// </summary>
    private static List<string> ReadServiceCapabilitiesV2(MetadataV2Service service)
    {
        if (service.Options is not null &&
            service.Options.TryGetValue("capabilities", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value);
                    }
                }
            }
            if (list.Count > 0)
            {
                return list;
            }
        }
        return ["Query"];
    }

    /// <summary>
    /// Reads the service's declared output formats from <c>service.Options["supportedFormats"]</c>.
    /// </summary>
    private static string[]? ReadServiceSupportedFormatsV2(MetadataV2Service service)
    {
        if (service.Options is not null &&
            service.Options.TryGetValue("supportedFormats", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value);
                    }
                }
            }
            return list.Count == 0 ? null : list.ToArray();
        }
        return null;
    }

    private static bool ServiceSupportsEditingV2(MetadataV2Service service)
    {
        var capabilities = ReadServiceCapabilitiesV2(service);
        foreach (var capability in capabilities)
        {
            if (capability.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("Delete", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("Editing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ServiceSupportsAdvancedQueriesV2(MetadataV2Service service)
        => ReadServiceCapabilitiesV2(service)
            .Any(capability => capability.Equals("Query", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the service opts into the sync/replica surface (createReplica /
    /// synchronizeReplica / extractChanges), driven by a declared <c>Sync</c>
    /// capability in <c>service.Options["capabilities"]</c>. The replica handlers
    /// are served unconditionally; this flag only advertises the surface so Esri
    /// clients enable their offline/replica workflows against the service.
    /// </summary>
    private static bool ServiceSupportsSyncV2(MetadataV2Service service)
        => ReadServiceCapabilitiesV2(service)
            .Any(capability => capability.Equals("Sync", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// V2 equivalent of <c>layer.SupportsAttachments</c>. V2 doesn't model attachments on the
    /// canonical resource shape; the resource opts in via <c>resource.Metadata.Annotations</c>
    /// (<c>honua.io/attachments=true</c>) or the legacy <c>"supportsAttachments"</c> annotation.
    /// </summary>
    private static bool ResourceSupportsAttachmentsV2(MetadataV2Resource resource)
    {
        // Canonical annotation key. Keep the legacy spelling alongside so resources migrated
        // verbatim from v1 still resolve.
        return TryReadBoolAnnotation(resource.Metadata.Annotations, "honua.io/attachments") ??
               TryReadBoolAnnotation(resource.Metadata.Annotations, "supportsAttachments") ??
               false;
    }

    private static bool? TryReadBoolAnnotation(IReadOnlyDictionary<string, string>? annotations, string key)
    {
        if (annotations is not null &&
            annotations.TryGetValue(key, out var raw) &&
            bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
