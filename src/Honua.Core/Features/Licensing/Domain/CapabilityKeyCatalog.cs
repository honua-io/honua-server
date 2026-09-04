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
/// enumerates Community-tier capabilities that ship ungated and descriptive
/// edition-qualified keys whose existing runtime gates are not owned by
/// <see cref="FeatureCatalog"/>.
/// </summary>
/// <param name="Key">Unique capability identifier (dot-namespaced, lowercase).</param>
/// <param name="DisplayName">Human-readable capability name.</param>
/// <param name="Category">Capability category for grouping.</param>
/// <param name="Edition">Minimum edition required to use this capability.</param>
/// <param name="Description">Brief description of the capability.</param>
/// <param name="Status">Optional release posture for keys whose live/experimental state is part of the public contract.</param>
public sealed record CapabilityKeyDefinition(
    string Key,
    string DisplayName,
    string Category,
    HonuaEdition Edition,
    string Description,
    string? Status = null);

/// <summary>
/// Canonical customer-facing capability vocabulary (#2893). Extends
/// <see cref="FeatureCatalog"/> with Community-tier and descriptive
/// edition-qualified capability keys. This type is purely a description layer:
/// it does not touch <c>LicenseGate</c> or any entitlement-enforcement code path.
/// <see cref="All"/> is the single list downstream consumers (the route→capability
/// mapping, the crosswalk artifact, honua-evidence, honua-site, and the SDK
/// coverage snapshots) must treat as authoritative; nothing else may fork it.
/// </summary>
public static class CapabilityKeyCatalog
{
    /// <summary>
    /// Release-posture value marking a capability as experimental. Experimental maturity is
    /// independent of deployability: routed keys remain valid deployment-profile inputs, while
    /// descriptive-only keys are excluded because no runtime route resolves them.
    /// </summary>
    public const string ExperimentalStatus = "experimental";

    /// <summary>Release-posture value marking a capability as Preview.</summary>
    public const string PreviewStatus = "preview";

    /// <summary>
    /// Edition-qualified, routed capabilities that remain deployable while their public release
    /// posture is experimental.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> RoutedExperimentalKeys { get; } =
    [
        new("serve.i3s-scene", "I3S Scene Serving", Categories.Serve,
            HonuaEdition.Enterprise, "Serve I3S metadata previews through Enterprise-gated SceneServer handlers; unlicensed requests return HTTP 402.", Status: ExperimentalStatus),
    ];

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

        /// <summary>Configured external data-provider implementations.</summary>
        public const string DataProviders = "DataProviders";

        /// <summary>Provider-neutral interchange and response formats.</summary>
        public const string Format = "Format";

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
            HonuaEdition.Community, "Discover GeocodeServer service and layer metadata. Batch geocode execution is gated by the geocoding.batch entitlement key (Enterprise); forward and reverse geocode execution are Community (#2981)."),
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
            HonuaEdition.Community, "Query environmental data resources through OGC API - Environmental Data Retrieval.", Status: PreviewStatus),
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

        // Provider-neutral response formats
        new("format.geoarrow", "GeoArrow Response Format", Categories.Format,
            HonuaEdition.Community, "Return FeatureServer query results as Arrow IPC with GeoArrow extension metadata through the shared provider-neutral response formatter.", Status: "live"),

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

        // Geocoding — Community (#2981). Forward and reverse single-address geocoding are the
        // demo/adoption showcase path; batch geocoding (geocoding.batch) remains an Enterprise
        // FeatureCatalog entitlement, gated over both HTTP and the MCP honua_geocode_addresses
        // tool. See ADR-0024.
        new("geocoding.forward", "Forward Geocoding", FeatureCatalog.Categories.Geocoding,
            HonuaEdition.Community, "Convert a freeform address to coordinates using configured providers."),
        new("geocoding.reverse", "Reverse Geocoding", FeatureCatalog.Categories.Geocoding,
            HonuaEdition.Community, "Convert coordinates to a nearest-address match using configured providers."),

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
    /// Descriptive keys that are edition-qualified but intentionally do not enter
    /// <see cref="FeatureCatalog"/> or change runtime entitlement enforcement. Warehouse
    /// providers remain experimental and fail closed behind their existing feature gates.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> DescriptiveKeys { get; } =
    [
        new("provider.redshift", "Amazon Redshift Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Amazon Redshift feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
        new("provider.snowflake", "Snowflake Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Snowflake feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
        new("provider.databricks", "Databricks SQL Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Databricks SQL feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
    ];

    /// <summary>
    /// The full canonical capability vocabulary: <see cref="CommunityKeys"/>,
    /// descriptive edition-qualified keys, plus
    /// every existing edition-gated key from <see cref="FeatureCatalog.All"/>,
    /// projected into the shared <see cref="CapabilityKeyDefinition"/> shape.
    /// This is the list every downstream consumer (route→capability mapping,
    /// crosswalk artifact, honua-evidence) must resolve against.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> All { get; } =
    [
        .. CommunityKeys,
        .. RoutedExperimentalKeys,
        .. DescriptiveKeys,
        .. FeatureCatalog.All.Select(static feature => new CapabilityKeyDefinition(
            feature.Key,
            feature.DisplayName,
            feature.Category,
            feature.MinimumEdition,
            feature.Description,
            Status: null)),
    ];

    /// <summary>
    /// The deployable subset of <see cref="All"/> — every key a deployment profile may enable.
    /// Descriptive keys are excluded because no route resolves to them, so enabling one narrows
    /// a deployment to nothing served (every request 404s at
    /// <c>UseDeploymentCapabilityProfile</c>) while still deriving a paid <c>requiredEdition</c>
    /// from the key's edition qualifier. They stay in <see cref="All"/> because the vocabulary
    /// is what the crosswalk artifact and honua-evidence resolve against; they are kept out of
    /// deployment inputs because that is the only place their lack of enforcement is harmful.
    /// </summary>
    public static IReadOnlyList<CapabilityKeyDefinition> DeployableKeys { get; } =
    [
        .. All.Except(DescriptiveKeys),
    ];
}
