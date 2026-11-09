// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public sealed record CapabilityKeyDefinition(
    string Key,
    string DisplayName,
    string Category,
    HonuaEdition Edition,
    string Description,
    string? Status = null);

public static class CapabilityKeyCatalog
{
    // Experimental maturity does not remove a routed key from deployment profiles; a descriptive-only key is excluded because no route resolves it.
    public const string ExperimentalStatus = "experimental";

    public const string PreviewStatus = "preview";

    // These capabilities run Honua's own hosted operation and are never offered to a licensee, because the Fair Core License prohibits providing Honua as a hosted service.
    public const string InternalStatus = "internal";

    public static IReadOnlyList<CapabilityKeyDefinition> RoutedExperimentalKeys { get; } =
    [
        new("serve.i3s-scene", "I3S Scene Serving", Categories.Serve,
            HonuaEdition.Enterprise, "Serve I3S metadata previews through Enterprise-gated SceneServer handlers; unlicensed requests return HTTP 402.", Status: ExperimentalStatus),
    ];

    public static IReadOnlyList<CapabilityKeyDefinition> RoutedPreviewKeys { get; } =
    [
        new("admin.multi-tenancy", "Multi-Tenant Operation", Categories.Internal,
            HonuaEdition.Enterprise, "Internal to Honua's own hosted operation; not part of the customer-deployable surface and not offered to licensees. Honua deployments are single-tenant. This has no path to general availability as a customer capability: the Fair Core License prohibits providing Honua to third parties as a hosted or managed service, so a licensee can never be the party operating it. Cross-tenant disclosure remains a full-severity security defect regardless.", Status: InternalStatus),

        // Operator ruling (2026-09-03, honua-release#266 / #264) supersedes the earlier
        // sync.offline GA promotion for release 2026.1: offline sync is Preview in 2026.1.
        new(FeatureCatalog.FieldOpsOfflineSyncKey, "Offline/Field Sync", FeatureCatalog.Categories.FieldOps,
            HonuaEdition.Pro, "Use disconnected field sync, form offline policy discovery, GeoServices replica delta sync delivered as Esri JSON, and FieldCollection cursor/change exchange.", Status: PreviewStatus),
    ];

    public static class Categories
    {
        public const string Serve = "Serve";

        public const string Discovery = "Discovery";

        public const string ControlPlane = "ControlPlane";

        // A licensee can never operate the hosted-only capabilities, and they are never published as customer documentation.
        public const string Internal = "Internal";

        public const string Ops = "Ops";

        public const string DataProviders = "DataProviders";

        public const string Format = "Format";

        public const string Process = "Process";

        public const string Collaboration = "Collaboration";

        public const string Demo = "Demo";

        public const string Enrichment = "Enrichment";

        public const string Jobs = "Jobs";
    }

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
            HonuaEdition.Community, "Serve coverage data through OGC API - Coverages.", Status: PreviewStatus),
        new("serve.ogc-api-records", "OGC API Records", Categories.Serve,
            HonuaEdition.Community, "Search and retrieve catalog records through OGC API - Records."),
        new("serve.ogc-api-edr", "OGC API - EDR", Categories.Serve,
            HonuaEdition.Community, "Query environmental data resources through OGC API - Environmental Data Retrieval.", Status: PreviewStatus),
        new("serve.odata", "OData v4", Categories.Serve,
            HonuaEdition.Community, "Query and edit features through the OData v4 protocol surface."),
        new("serve.wms", "WMS 1.3", Categories.Serve,
            HonuaEdition.Community, "Serve map images through WMS 1.3 (GetMap, GetFeatureInfo, GetCapabilities)."),
        new("serve.wmts", "WMTS 1.0", Categories.Serve,
            HonuaEdition.Community, "Serve pre-rendered tile pyramids through WMTS 1.0.", Status: PreviewStatus),
        new("serve.wcs", "WCS 2.0.1", Categories.Serve,
            HonuaEdition.Community, "Serve coverage data through WCS 2.0.1."),
        new("serve.wfs", "WFS 2.0", Categories.Serve,
            HonuaEdition.Community, "Query and read features through WFS 2.0."),
        new("serve.wps", "WPS 2.0", Categories.Serve,
            HonuaEdition.Community, "Submit and poll processes through OGC WPS 2.0 (GetCapabilities, DescribeProcess, Execute, GetStatus, GetResult).", Status: PreviewStatus),
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
        new("serve.grpc", "gRPC (geospatial.v1)", Categories.Serve,
            HonuaEdition.Community, "The geospatial.v1 gRPC surface — FeatureService, ProcessService, SpecService, SceneService, TileService and ElevationService — served natively over h2c on port 8081 and as gRPC-Web on 8080. Advertised as transport.grpc, transport.grpc-web and transport.native-grpc on the capability manifest, which are wire framings of this one capability."),

        // Jobs
        new("jobs.durable-runtime", "Durable Job Runtime", Categories.Jobs,
            HonuaEdition.Community, "The durable job substrate behind imports, tile operations, geoprocessing and workflow orchestration. Advertised as jobs.runner on the honua.capability_manifest.v1 wire and named by typed dependency-unavailable refusals, so a client receiving one can resolve the id. The runtime itself is Community; durable persistence across restarts and nodes requires Redis, which caching.redis gates."),

        // Discovery
        new("discovery.catalog", "Catalog Discovery", Categories.Discovery,
            HonuaEdition.Community, "Read configured catalogs with caller-filtered items, search and facets."),
        new("discovery.capability-manifest", "Capability Manifest", Categories.Discovery,
            HonuaEdition.Community, "Advertise the server's capability manifest for SDK/agent discovery (#1186)."),

        // Control plane
        new("admin.control-plane", "Admin Control Plane", Categories.ControlPlane,
            HonuaEdition.Community, "General administrative CRUD surfaces (connections, metadata, services, users, roles, configuration) with no dedicated entitlement of their own. Isolation between administrative scopes remains mandatory."),
        new("operations.proposals", "Governed Operation Proposals", Categories.ControlPlane,
            HonuaEdition.Community, "The governed operation proposal and approval control plane behind approval-gated honua_admin_* tools, /api/v1/admin/proposals and Studio drafts. Advertised as operations.proposals on the honua.capability_manifest.v1 wire. The control plane itself is Community; proposals persist in the Redis-backed durable store, so a host without Redis (or without the caching.redis entitlement) advertises the governed tools but refuses proposal-requiring calls with a typed capability-unavailable receipt."),

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
            HonuaEdition.Community, "Submit and poll geoprocessing tasks through the Esri GeoServices GPServer surface. Print/export-specific tasks are gated by printing.* entitlement keys.", Status: PreviewStatus),
        new("process.ogc-api-processes", "OGC API Processes", Categories.Process,
            HonuaEdition.Community, "Submit and poll jobs through OGC API - Processes.", Status: PreviewStatus),

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

    public static IReadOnlyList<CapabilityKeyDefinition> DescriptiveKeys { get; } =
    [
        new("provider.redshift", "Amazon Redshift Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Amazon Redshift feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
        new("provider.snowflake", "Snowflake Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Snowflake feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
        new("provider.databricks", "Databricks SQL Provider", Categories.DataProviders,
            HonuaEdition.Enterprise, "Experimental, off-by-default Databricks SQL feature provider; enabling the capability key alone does not enable the provider.", Status: ExperimentalStatus),
    ];

    public static IReadOnlyList<CapabilityKeyDefinition> All { get; } =
    [
        .. CommunityKeys,
        .. RoutedExperimentalKeys,
        .. RoutedPreviewKeys,
        .. DescriptiveKeys,
        .. FeatureCatalog.All
            .Where(static feature => !RoutedPreviewKeys.Any(
                preview => string.Equals(preview.Key, feature.Key, StringComparison.Ordinal)))
            .Select(static feature => new CapabilityKeyDefinition(
            feature.Key,
            feature.DisplayName,
            feature.Category,
            feature.MinimumEdition,
            feature.Description,
            Status: IsCustomerAlerting(feature) ? PreviewStatus : null)),
    ];

    private static bool IsCustomerAlerting(FeatureDefinition feature)
        => string.Equals(feature.Category, FeatureCatalog.Categories.Alerts, StringComparison.Ordinal)
            || string.Equals(feature.Category, FeatureCatalog.Categories.Channels, StringComparison.Ordinal);

    // Descriptive keys are excluded from deployment profiles because enabling one would make every request miss while still deriving a paid edition.
    public static IReadOnlyList<CapabilityKeyDefinition> DeployableKeys { get; } =
    [
        .. All.Except(DescriptiveKeys),
    ];
}
