// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Shared builder/publisher for the canonical <c>mobile_offline_demo</c> Metadata v2 graph
/// (layers 68910/68920) used by both the FeatureServer and OGC API Features regression
/// suites for honua-server#1238.
/// <para>
/// The seeded layers store every non-key field as a key inside the shared
/// <c>features.attributes</c> JSONB column. The storage binding must therefore declare
/// <c>attributesColumn=attributes</c> so the storage-mapped reader projects
/// <c>attributes-&gt;&gt;'field'</c> instead of bare columns (which produce Postgres 42703).
/// The <c>includeAttributesAccessor</c> flag lets a test reproduce the broken drift
/// (no accessor =&gt; bare columns =&gt; 42703) and then prove the fix.
/// </para>
/// </summary>
public static class MobileOfflineDemoGraphPublisher
{
    public const string ServiceName = "mobile_offline_demo";
    public const int OfflineSitesLayerId = 68910;
    public const int OfflineZonesLayerId = 68920;

    private static readonly string[] Capabilities =
        ["Query", "Extract", "Create", "Update", "Delete", "Editing", "Sync"];
    private static readonly string[] SupportedFormats = ["JSON", "GeoJSON"];

    /// <summary>
    /// Replaces the mobile-offline service/resources/bindings/publications in the supplied
    /// graph with FeatureServer and OGC API Features projections of layers 68910/68920.
    /// </summary>
    public static void Publish(
        TestMetadataV2GraphProvider provider,
        bool includeAttributesAccessor = true)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var graph = snapshot.Graph;

        var accessPolicy = new AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true
        };

        var featureService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-mobile-offline-demo-feature",
                Name = ServiceName,
                Title = ServiceName,
                Description = "Deterministic SDK-backed mobile offline field operations fixture"
            },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = $"/rest/services/{ServiceName}/FeatureServer",
            Protocols = [MetadataV2ServiceProtocols.FeatureServer],
            AccessPolicy = accessPolicy,
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(Capabilities),
                ["supportedFormats"] = JsonSerializer.SerializeToElement(SupportedFormats)
            }
        };

        var ogcService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-mobile-offline-demo-ogc",
                Name = ServiceName,
                Title = ServiceName,
                Description = "OGC API Features projection of the mobile offline fixture"
            },
            ServiceType = MetadataV2ServiceType.OgcApiFeatures,
            Route = "/ogc/features",
            Protocols = [MetadataV2ServiceProtocols.OgcFeatures],
            AccessPolicy = accessPolicy,
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };

        var siteResource = CreateResource(
            layerId: OfflineSitesLayerId,
            name: "Offline Field Sites",
            description: "Mobile-editable point layer for SDK offline create, update, delete, and conflict scenarios.",
            geometryType: MetadataV2GeometryType.Point,
            bbox: new MetadataV2Bbox { West = -158.1250, South = 21.2600, East = -157.8500, North = 21.4300 },
            displayField: "site_name",
            temporalField: "inspection_date",
            fields:
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, "Object ID", ["id.primary"]),
                Field("globalid", MetadataV2FieldType.String, nullable: false, "Stable mobile client/global identifier", length: 64),
                Field("site_name", MetadataV2FieldType.String, nullable: false, "Field site name", length: 128),
                Field("status", MetadataV2FieldType.String, nullable: false, "Workflow status", length: 32),
                Field("priority", MetadataV2FieldType.String, nullable: false, "Dispatch priority", length: 32),
                Field("assigned_to", MetadataV2FieldType.String, nullable: true, "Assigned mobile user", length: 64),
                Field("inspection_date", MetadataV2FieldType.DateTime, nullable: false, "Inspection timestamp"),
                Field("sync_version", MetadataV2FieldType.Integer, nullable: false, "Deterministic offline conflict token"),
                Field("offline_action", MetadataV2FieldType.String, nullable: false, "Fixture role for offline harness", length: 32),
                Field("notes", MetadataV2FieldType.String, nullable: true, "Operator notes", length: 1024),
                Field("shape", MetadataV2FieldType.Geometry, nullable: true, "Geometry", ["geometry.primary"])
            ],
            accessPolicy);

        var zoneResource = CreateResource(
            layerId: OfflineZonesLayerId,
            name: "Offline Work Zones",
            description: "Polygon context layer included in the offline package as readonly operational context.",
            geometryType: MetadataV2GeometryType.Polygon,
            bbox: new MetadataV2Bbox { West = -158.1250, South = 21.2600, East = -157.7000, North = 21.5200 },
            displayField: "zone_name",
            temporalField: null,
            fields:
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, "Object ID", ["id.primary"]),
                Field("globalid", MetadataV2FieldType.String, nullable: false, "Stable zone identifier", length: 64),
                Field("zone_name", MetadataV2FieldType.String, nullable: false, "Work zone name", length: 128),
                Field("zone_status", MetadataV2FieldType.String, nullable: false, "Zone status", length: 32),
                Field("sync_version", MetadataV2FieldType.Integer, nullable: false, "Readonly context generation token"),
                Field("notes", MetadataV2FieldType.String, nullable: true, "Zone notes", length: 1024),
                Field("shape", MetadataV2FieldType.Geometry, nullable: true, "Geometry", ["geometry.primary"])
            ],
            accessPolicy);

        var storageBindings = new[]
        {
            CreateStorageBinding(OfflineSitesLayerId, includeAttributesAccessor),
            CreateStorageBinding(OfflineZonesLayerId, includeAttributesAccessor)
        };

        var publications = new[]
        {
            CreateFeaturePublication(OfflineSitesLayerId),
            CreateFeaturePublication(OfflineZonesLayerId),
            CreateOgcPublication(OfflineSitesLayerId),
            CreateOgcPublication(OfflineZonesLayerId)
        };

        var ownedServiceIds = new[] { "svc-mobile-offline-demo-feature", "svc-mobile-offline-demo-ogc" };
        var ownedResourceIds = new[] { ResourceId(OfflineSitesLayerId), ResourceId(OfflineZonesLayerId) };
        var ownedBindingIds = new[] { BindingId(OfflineSitesLayerId), BindingId(OfflineZonesLayerId) };
        var ownedPublicationIds = publications.Select(p => p.Metadata.Id).ToHashSet(StringComparer.Ordinal);

        provider.SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services
                .Where(existing => !ownedServiceIds.Contains(existing.Metadata.Id))
                .Append(featureService)
                .Append(ogcService)
                .ToArray(),
            Resources = graph.Resources
                .Where(existing => !ownedResourceIds.Contains(existing.Metadata.Id))
                .Append(siteResource)
                .Append(zoneResource)
                .ToArray(),
            StorageBindings = graph.StorageBindings
                .Where(existing => !ownedBindingIds.Contains(existing.Metadata.Id))
                .Concat(storageBindings)
                .ToArray(),
            Publications = graph.Publications
                .Where(existing => !ownedPublicationIds.Contains(existing.Metadata.Id))
                .Concat(publications)
                .ToArray()
        });
    }

    private static string ResourceId(int layerId)
        => $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string BindingId(int layerId)
        => $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static MetadataV2Resource CreateResource(
        int layerId,
        string name,
        string description,
        MetadataV2GeometryType geometryType,
        MetadataV2Bbox bbox,
        string displayField,
        string? temporalField,
        IReadOnlyList<MetadataV2Field> fields,
        AccessPolicy accessPolicy)
    {
        var bindingId = BindingId(layerId);
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = ResourceId(layerId),
                Name = name,
                Title = name,
                Description = description
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            AccessPolicy = accessPolicy,
            SchemaFields = fields,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                Bbox = bbox,
                PrimaryGeometryField = "shape"
            },
            Temporal = temporalField is null
                ? null
                : new MetadataV2ResourceTemporal { StartTimeField = temporalField },
            Display = new MetadataV2ResourceDisplay { DisplayField = displayField },
            Editing = new MetadataV2ResourceEditing { CanModify = true }
        };
    }

    private static MetadataV2StorageBinding CreateStorageBinding(int layerId, bool includeAttributesAccessor)
    {
        // The seed's shared 'features' table holds rows for every layer keyed by a 'layer_id'
        // discriminator; geometry lives in the 'geometry' column and the declared fields are
        // persisted in the 'attributes' JSONB column. Expose geometry/attributes columns via
        // Options (ParseRelationalLocator rejects locators containing ':').
        var options = new Dictionary<string, JsonElement>
        {
            ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
            // The shared 'features' table holds rows for every layer keyed by 'layer_id'.
            // Declaring the discriminator column makes the storage-mapped reader constrain
            // each read to this binding's StorageLayerId, preventing cross-layer leakage
            // (honua-server#1238 follow-up).
            ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
        };

        if (includeAttributesAccessor)
        {
            options["attributesColumn"] = JsonSerializer.SerializeToElement("attributes");
        }

        return new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BindingId(layerId),
                Name = BindingId(layerId)
            },
            ResourceId = ResourceId(layerId),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "features",
            StorageLayerId = layerId,
            Options = options,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate,
                MetadataV2StorageBindingCapability.Edit,
                MetadataV2StorageBindingCapability.Transactions
            ]
        };
    }

    private static MetadataV2Publication CreateFeaturePublication(int layerId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"pub-mobile-offline-feature-{layerId.ToString(CultureInfo.InvariantCulture)}",
                Name = layerId.ToString(CultureInfo.InvariantCulture)
            },
            ServiceId = "svc-mobile-offline-demo-feature",
            ResourceId = ResourceId(layerId),
            StorageBindingId = BindingId(layerId),
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerId.ToString(CultureInfo.InvariantCulture),
                IsNumeric = true
            },
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer
        };

    private static MetadataV2Publication CreateOgcPublication(int layerId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"pub-mobile-offline-ogc-{layerId.ToString(CultureInfo.InvariantCulture)}",
                Name = layerId.ToString(CultureInfo.InvariantCulture)
            },
            ServiceId = "svc-mobile-offline-demo-ogc",
            ResourceId = ResourceId(layerId),
            StorageBindingId = BindingId(layerId),
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerId.ToString(CultureInfo.InvariantCulture),
                IsNumeric = true
            },
            PublicationType = MetadataV2PublicationType.OgcCollection
        };

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable,
        string description,
        IReadOnlyList<string>? semanticRoles = null,
        int? length = null)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            Description = description,
            SemanticRoles = semanticRoles ?? [],
            Length = length
        };
}
