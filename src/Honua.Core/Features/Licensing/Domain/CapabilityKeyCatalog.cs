// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Definition of a customer-facing capability key — the canonical, dot-namespaced
/// <c>&lt;category&gt;.&lt;name&gt;</c> vocabulary a buyer recognizes (issue #2893,
/// epic #2892). This is a strictly broader vocabulary than
/// <see cref="FeatureCatalog"/>: <see cref="FeatureCatalog"/> enumerates only the
/// edition-gated (Pro/Enterprise) entitlement keys enforced by
/// <c>LicenseGate</c>, while <see cref="CapabilityKeyCatalog"/> additionally
/// enumerates the Community-tier capabilities that ship ungated — serve/query
/// surfaces per protocol family, file import, discovery, and other surfaces a
/// prospect's capability checklist needs to reference even though no license
/// check exists for them.
/// </summary>
/// <param name="Key">Unique capability identifier (dot-namespaced, lowercase).</param>
/// <param name="DisplayName">Human-readable capability name.</param>
/// <param name="Category">Capability category for grouping.</param>
/// <param name="Edition">Minimum edition required to use this capability.</param>
/// <param name="Description">Brief description of the capability.</param>
public sealed record CapabilityKeyDefinition(
    string Key,
    string DisplayName,
    string Category,
    HonuaEdition Edition,
    string Description);

/// <summary>
/// Canonical customer-facing capability vocabulary (#2893). Extends
/// <see cref="FeatureCatalog"/> with the Community-tier capability keys that
/// have no entitlement gate — this type is purely a description layer: it does
/// not touch <c>LicenseGate</c> or any entitlement-enforcement code path.
/// <see cref="All"/> is the single list downstream consumers (the route→capability
/// mapping, the crosswalk artifact, honua-evidence, honua-site, and the SDK
/// coverage snapshots) must treat as authoritative; nothing else may fork it.
/// </summary>
public static class CapabilityKeyCatalog
{
    /// <summary>
    /// Capability categories introduced alongside the existing
    /// <see cref="FeatureCatalog.Categories"/> set. Community-only groupings
    /// that do not correspond to an edition-gated feature category.
    /// </summary>
    public static class Categories
    {
        /// <summary>Protocol/query serve surfaces (Community-tier "you can reach this protocol").</summary>
        public const string Serve = "Serve";

        /// <summary>Capability/service discovery and metadata-advertisement surfaces.</summary>
        public const string Discovery = "Discovery";

        /// <summary>General admin/control-plane CRUD surfaces with no dedicated entitlement.</summary>
        public const string ControlPlane = "ControlPlane";

        /// <summary>Operational health, metrics, and observability surfaces.</summary>
        public const string Ops = "Ops";

        /// <summary>Job/process execution surfaces (geoprocessing, OGC API Processes).</summary>
        public const string Process = "Process";

        /// <summary>Collaborative map/content session surfaces.</summary>
        public const string Collaboration = "Collaboration";

        /// <summary>Showcase/demo-only surfaces (not a sellable capability).</summary>
        public const string Demo = "Demo";

        /// <summary>Third-party data enrichment dataset surfaces.</summary>
        public const string Enrichment = "Enrichment";
    }

    /// <summary>
    /// The Community-tier capability keys this catalog adds. Every entry here
    /// carries <see cref="HonuaEdition.Community"/> — Pro/Enterprise capabilities
    /// remain sourced exclusively from <see cref="FeatureCatalog.All"/> so the
    /// entitlement vocabulary has exactly one owner.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> CommunityKeys { get; } =
    [
        // Serve — GeoServices REST protocol surfaces (query/read side; write
        // surfaces stay on the existing editing.featureserver-edits Pro key).
        new("serve.geoservices-root", "GeoServices REST Root", Categories.Serve,
            HonuaEdition.Community, "Discover the GeoServices REST catalog root and service-directory info."),
        new("serve.geoservices-featureserver", "FeatureServer Query", Categories.Serve,
            HonuaEdition.Community, "Query and read features through the Esri GeoServices FeatureServer surface (query, metadata, related records, domains, shared templates)."),
        new("serve.geoservices-mapserver", "MapServer", Categories.Serve,
            HonuaEdition.Community, "Serve map images, identify, and export through the Esri GeoServices MapServer surface."),
        new("serve.geoservices-imageserver", "ImageServer", Categories.Serve,
            HonuaEdition.Community, "Serve raster imagery and coverage metadata through the Esri GeoServices ImageServer surface."),
        new("serve.geoservices-geometry-service", "Geometry Service", Categories.Serve,
            HonuaEdition.Community, "Server-side geometry operations (project, buffer, simplify, union, and related utilities) through the Esri GeometryServer surface."),
        new("serve.geoservices-geocodeserver", "GeocodeServer Discovery", Categories.Serve,
            HonuaEdition.Community, "Discover GeocodeServer service and layer metadata. Forward/reverse/batch geocode execution is gated by the geocoding.* entitlement keys."),
        new("serve.geoservices-vectortileserver", "VectorTileServer", Categories.Serve,
            HonuaEdition.Community, "Serve Esri vector tile services through the GeoServices VectorTileServer surface."),
        new("serve.ogc-api-features", "OGC API Features", Categories.Serve,
            HonuaEdition.Community, "Read and query collections/items through OGC API - Features. Mutation is Community via the shared edit pipeline (distinct from the Pro FeatureServer write gate)."),
        new("serve.ogc-api-maps", "OGC API Maps", Categories.Serve,
            HonuaEdition.Community, "Render maps through OGC API - Maps."),
        new("serve.ogc-api-tiles", "OGC API Tiles", Categories.Serve,
            HonuaEdition.Community, "Serve vector and raster tiles through OGC API - Tiles."),
        new("serve.ogc-api-coverages", "OGC API Coverages", Categories.Serve,
            HonuaEdition.Community, "Serve coverage data through OGC API - Coverages."),
        new("serve.ogc-api-records", "OGC API Records", Categories.Serve,
            HonuaEdition.Community, "Search and retrieve catalog records through OGC API - Records."),
        new("serve.ogc-api-edr", "OGC API - EDR", Categories.Serve,
            HonuaEdition.Community, "Query environmental data resources through OGC API - Environmental Data Retrieval."),
        new("serve.odata", "OData v4", Categories.Serve,
            HonuaEdition.Community, "Query and edit features through the OData v4 protocol surface."),
        new("serve.wms", "WMS 1.3", Categories.Serve,
            HonuaEdition.Community, "Serve map images through WMS 1.3 (GetMap, GetFeatureInfo, GetCapabilities)."),
        new("serve.wmts", "WMTS 1.0", Categories.Serve,
            HonuaEdition.Community, "Serve pre-rendered tile pyramids through WMTS 1.0."),
        new("serve.wcs", "WCS 2.0.1", Categories.Serve,
            HonuaEdition.Community, "Serve coverage data through WCS 2.0.1."),
        new("serve.wfs", "WFS 2.0", Categories.Serve,
            HonuaEdition.Community, "Query and read features through WFS 2.0."),
        new("serve.vector-tiles", "Vector Tiles (MVT/TileJSON/PMTiles)", Categories.Serve,
            HonuaEdition.Community, "Serve Mapbox Vector Tiles, TileJSON descriptors, and PMTiles archives."),
        new("serve.sensorthings", "OGC SensorThings API", Categories.Serve,
            HonuaEdition.Community, "Query sensor observation data through OGC SensorThings API v1.1."),
        new("serve.stac", "STAC API", Categories.Serve,
            HonuaEdition.Community, "Search and browse spatiotemporal asset catalogs through the STAC API."),
        new("serve.3d-tiles-scene", "3D Tiles Scene Serving", Categories.Serve,
            HonuaEdition.Community, "Serve published 3D Tiles scene layers through the SceneServer surface. Scene ingest (CityGML/point cloud) is Enterprise-gated separately."),
        new("serve.i3s-scene", "I3S Scene Serving", Categories.Serve,
            HonuaEdition.Community, "Serve published I3S scene layers Esri clients consume directly."),
        new("serve.elevation", "Elevation Query", Categories.Serve,
            HonuaEdition.Community, "Query elevation profile and point-value surfaces. Sun/shadow, slice, line-of-sight, and viewshed analytics are Pro-gated separately."),

        // Discovery
        new("discovery.capability-manifest", "Capability Manifest", Categories.Discovery,
            HonuaEdition.Community, "Advertise the server's capability manifest for SDK/agent discovery (#1186)."),

        // Control plane
        new("admin.control-plane", "Admin Control Plane", Categories.ControlPlane,
            HonuaEdition.Community, "General administrative CRUD surfaces (connections, metadata, services, tenants, users, roles, configuration) with no dedicated entitlement of their own."),

        // Ops
        new("ops.health", "Health Checks", Categories.Ops,
            HonuaEdition.Community, "Liveness/readiness health-check endpoints."),
        new("ops.observability", "Observability", Categories.Ops,
            HonuaEdition.Community, "Metrics and monitoring surfaces for operational visibility."),

        // Process
        new("process.geoprocessing", "Geoprocessing Task Execution", Categories.Process,
            HonuaEdition.Community, "Submit and poll geoprocessing tasks through the Esri GeoServices GPServer surface. Print/export-specific tasks are gated by printing.* entitlement keys."),
        new("process.ogc-api-processes", "OGC API Processes", Categories.Process,
            HonuaEdition.Community, "Submit and poll jobs through OGC API - Processes."),

        // Collaboration
        new("collaboration.map-sessions", "Map Collaboration Sessions", Categories.Collaboration,
            HonuaEdition.Community, "Collaborative saved-map session operations (comments, activity, shared editing coordination)."),

        // Demo
        new("demo.showcase", "Demo Showcase Surfaces", Categories.Demo,
            HonuaEdition.Community, "Non-sellable demo/showcase endpoints used for product demonstrations."),

        // Enrichment
        new("enrichment.datasets", "Data Enrichment Datasets", Categories.Enrichment,
            HonuaEdition.Community, "Register and manage third-party data-enrichment dataset sources."),

        // Field ops
        new("fieldops.forms", "Field Collection Forms", FeatureCatalog.Categories.FieldOps,
            HonuaEdition.Community, "Read and submit online form packages. Disconnected/offline sync is Pro-gated separately by fieldops.offline-sync."),

        // Identity: identity.saml and identity.scim moved to FeatureCatalog entitlements
        // (both Enterprise, per ADR-0024's Identity Governance tier) in #2978 — they
        // previously shipped here ungated, catalog drift from the ADR that let any
        // SAML-speaking IdP bypass SSO tiering. They flow back into All via the
        // FeatureCatalog union below.

        // Styling
        new("styling.ogc-api-styles", "OGC API Styles", FeatureCatalog.Categories.Styling,
            HonuaEdition.Community, "Read and manage styles through OGC API - Styles (Part 1)."),

        // Scene
        new("scene.catalog", "Scene Catalog", FeatureCatalog.Categories.Scene,
            HonuaEdition.Community, "Discover published 3D scene layers through the scene catalog surface."),

        // Raster
        new("raster.terrain-rgb", "Terrain-RGB Tiles", FeatureCatalog.Categories.Raster,
            HonuaEdition.Community, "Serve Terrain-RGB encoded elevation tiles."),

        // Analytics
        new("analytics.content", "Analysis Artifact Content", FeatureCatalog.Categories.Analytics,
            HonuaEdition.Community, "Store and retrieve spatial-analysis artifacts. The analytics compute operations themselves (clustering, spatial join, etc.) remain Pro-gated."),
        new("analytics.reporting", "Analysis Reporting", FeatureCatalog.Categories.Analytics,
            HonuaEdition.Community, "Retrieve generated analysis reports."),

        // AI
        new("ai.spec-artifacts", "Spec Artifact Retrieval", FeatureCatalog.Categories.Ai,
            HonuaEdition.Community, "Read-only retrieval of executable spec artifacts by content hash. Applying a spec plan remains Pro-gated (ai.spec-apply)."),
    ];

    /// <summary>
    /// The full canonical capability vocabulary: <see cref="CommunityKeys"/> plus
    /// every existing edition-gated key from <see cref="FeatureCatalog.All"/>,
    /// projected into the shared <see cref="CapabilityKeyDefinition"/> shape.
    /// This is the list every downstream consumer (route→capability mapping,
    /// crosswalk artifact, honua-evidence) must resolve against.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> All { get; } =
    [
        .. CommunityKeys,
        .. FeatureCatalog.All.Select(static feature => new CapabilityKeyDefinition(
            feature.Key,
            feature.DisplayName,
            feature.Category,
            feature.MinimumEdition,
            feature.Description)),
    ];
}
