// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Definition of an edition-gated platform feature.
/// </summary>
/// <param name="Key">Unique feature identifier</param>
/// <param name="DisplayName">Human-readable feature name</param>
/// <param name="Category">Feature category for grouping</param>
/// <param name="MinimumEdition">Minimum edition required to enable this feature</param>
/// <param name="Description">Brief description of the feature</param>
public sealed record FeatureDefinition(
    string Key,
    string DisplayName,
    string Category,
    HonuaEdition MinimumEdition,
    string Description);

/// <summary>
/// Static catalog of all edition-gated features in the Honua platform.
/// </summary>
public static class FeatureCatalog
{
    /// <summary>
    /// Feature categories used for grouping in the admin UI.
    /// </summary>
    public static class Categories
    {
        /// <summary>Alerting and geofence features.</summary>
        public const string Alerts = "Alerts";

        /// <summary>Alert delivery channel features.</summary>
        public const string Channels = "Channels";

        /// <summary>Data import and ingestion features.</summary>
        public const string Import = "Import";

        /// <summary>Geocoding and address resolution features.</summary>
        public const string Geocoding = "Geocoding";

        /// <summary>Network routing and service-area analysis features.</summary>
        public const string Routing = "Routing";

        /// <summary>Identity and authentication features.</summary>
        public const string Identity = "Identity";

        /// <summary>Caching and performance features.</summary>
        public const string Caching = "Caching";

        /// <summary>Static map image rendering features.</summary>
        public const string StaticMap = "StaticMap";

        /// <summary>Styling and cartographic features.</summary>
        public const string Styling = "Styling";

        /// <summary>Raster and imagery features.</summary>
        public const string Raster = "Raster";

        /// <summary>Spatial analytics — clustering, joins, density, buffer aggregate.</summary>
        public const string Analytics = "Analytics";

        /// <summary>Real-time feature streaming and subscriptions.</summary>
        public const string Streaming = "Streaming";

        /// <summary>3D scene features — CityGML/BIM ingest and Building Scene Layer publishing.</summary>
        public const string Scene = "Scene";

        /// <summary>Temporal animation, time-aware filtering, and time-series tile features.</summary>
        public const string Temporal = "Temporal";

        /// <summary>Printing and layout export features.</summary>
        public const string Printing = "Printing";

        /// <summary>Editing features — branch versioning, reconcile/post, multi-user editing.</summary>
        public const string Editing = "Editing";

        /// <summary>Offline field operations and disconnected sync features.</summary>
        public const string FieldOps = "FieldOps";

        /// <summary>Agentic AI operations — MCP discovery/query, spec plan/apply, grounding, workflow generation, and the agent-operations guardrail ladder.</summary>
        public const string Ai = "AI";

        /// <summary>Server extensibility features — plugin/extension SDK.</summary>
        public const string Extensibility = "Extensibility";

        /// <summary>High availability and disaster recovery — backup automation, failover, RTO/RPO reporting.</summary>
        public const string DisasterRecovery = "DisasterRecovery";
    }

    /// <summary>
    /// Entitlement key for automated PostgreSQL backups — scheduled base backups plus
    /// WAL archiving that enable point-in-time recovery (#356, ADR-0024). Enterprise-only.
    /// </summary>
    public const string BackupAutomationKey = "dr.backup-automation";

    /// <summary>
    /// Entitlement key for active-passive failover playbooks driven by automated health
    /// checks against the primary serving surface (#356, ADR-0024). Enterprise-only.
    /// </summary>
    public const string FailoverPlaybooksKey = "dr.failover";

    /// <summary>
    /// Entitlement key for Redis cache-state backup and restore so warm cache contents
    /// survive a regional failover (#356, ADR-0024). Enterprise-only.
    /// </summary>
    public const string CacheBackupKey = "dr.cache-backup";

    /// <summary>
    /// Entitlement key for RTO/RPO objective tracking and recovery-readiness reporting that
    /// surfaces last-successful-backup, restorable point, and objective compliance to the
    /// admin observability surface (#356, ADR-0024). Enterprise-only.
    /// </summary>
    public const string RecoveryReportingKey = "dr.rto-rpo-reporting";

    /// <summary>
    /// Entitlement key for agent-initiated operations under the Pro validation
    /// layer (#1631, #1592): spec apply execution, NL grounding mutate surfaces,
    /// AI workflow generation, and MCP execute/mutate tools. Pro and above run
    /// agent changes through the validation layer (plan + dry-run + validate with
    /// a pre-change snapshot/backup gate) before apply. MCP discovery/query and
    /// agent reads remain Community and carry no gate.
    /// </summary>
    public const string AiOperationsKey = "ai.agent-operations";

    /// <summary>
    /// Entitlement key for human-in-the-loop approval workflows over
    /// agent-initiated operations (#1631). Enterprise-only: layers approval,
    /// policy-scoped agent permissions, and immutable audit on top of the Pro
    /// validation layer.
    /// </summary>
    public const string AiApprovalWorkflowsKey = "ai.approval-workflows";

    /// <summary>
    /// Entitlement key for MCP discovery and query — the read/search/analyze
    /// agent surface. Community-tier and always active: preserves the adoption
    /// thesis that the work goes to agents on the free tier (#1592). Present in
    /// the catalog so the capability manifest can advertise it explicitly.
    /// </summary>
    public const string McpDiscoveryKey = "ai.mcp-discovery";

    /// <summary>
    /// Entitlement key for Esri-style branch versioning (named gdb versions, version read/edit
    /// sessions, reconcile/post). Gates the GeoServices VersionManagementServer surface and
    /// <c>gdbVersion</c>-scoped FeatureServer editing (#1272, ADR-0051). Postgres-only.
    /// </summary>
    public const string BranchVersioningKey = "editing.branch-versioning";

    /// <summary>
    /// Entitlement key for editing through the Esri GeoServices FeatureServer write surface —
    /// applyEdits/addFeatures/updateFeatures/deleteFeatures and the calculate bulk field update.
    /// Pro-tier (#1591): the Esri-compatibility premium is paid on the write side only. Feature
    /// editing via the open protocols (OGC API Features mutations, WFS-T, OData CRUD/$batch, and
    /// gRPC edits) is Community and carries no entitlement gate, while still flowing through the
    /// shared edit/transaction pipeline. Enterprise branch versioning
    /// (<c>editing.branch-versioning</c>) is a separate entitlement.
    /// </summary>
    public const string FeatureServerEditsKey = "editing.featureserver-edits";

    /// <summary>
    /// Entitlement key for disconnected field operations — form offline policy discovery,
    /// FieldCollection cursor/change sync, and GeoServices replica/GeoPackage delta sync.
    /// Online form package reads and submissions remain Community surfaces.
    /// </summary>
    public const string FieldOpsOfflineSyncKey = "fieldops.offline-sync";

    /// <summary>
    /// Entitlement key for applying executable spec plans and MCP execution tools that submit
    /// server-side agentic work. Spec validation, planning, discovery, and read/query paths stay
    /// Community.
    /// </summary>
    public const string AiSpecApplyKey = "ai.spec-apply";

    /// <summary>
    /// Entitlement key for natural-language spec mutation grounding. Deterministic summaries and
    /// discovery paths remain Community.
    /// </summary>
    public const string AiGroundingKey = "ai.grounding";

    /// <summary>
    /// Entitlement key for natural-language generation of workflows, maps, apps, forms, reports,
    /// dashboards, saved queries, and analysis packages.
    /// </summary>
    public const string AiWorkflowGenerationKey = "ai.workflow-generation";

    /// <summary>
    /// Entitlement key for the server plugin/extension SDK (custom feature validators and edit
    /// hooks today; computed fields and custom endpoints in later phases). Gates compile-time,
    /// AOT-safe plugins registered at startup (#347, ADR-0024). Enterprise-only.
    /// </summary>
    public const string PluginSdkKey = "plugin.sdk";

    /// <summary>
    /// Entitlement key for basic single-provider OpenID Connect authentication. Pro-tier:
    /// one configured Azure AD, Google, Okta, Auth0, or generic OIDC provider.
    /// </summary>
    public const string OidcAuthenticationKey = "identity.oidc";

    /// <summary>
    /// Entitlement key for configuring multiple OIDC identity providers in one deployment.
    /// Enterprise-only identity governance; basic single-provider OIDC is separately gated
    /// by <see cref="OidcAuthenticationKey"/>.
    /// </summary>
    public const string OidcMultiProviderKey = "identity.oidc-multi-provider";

    /// <summary>
    /// Entitlement key for custom OIDC claim-to-role mapping. Enterprise-only identity
    /// governance; default role assignment for basic single-provider OIDC remains Pro.
    /// </summary>
    public const string OidcClaimsMappingKey = "identity.claims-mapping";

    /// <summary>
    /// Entitlement key for native mTLS client-certificate authentication (#2431). Enterprise-only:
    /// gates the client-certificate trust-profile admin surface
    /// (<c>/api/v1/admin/security/client-certificates/*</c>) and the enforcement pipeline that maps
    /// a presented client certificate to a Honua principal.
    /// </summary>
    public const string MtlsClientCertificateKey = "identity.mtls-client-certificate";

    /// <summary>
    /// Entitlement key for CityGML/BIM ingest into a servable Building Scene
    /// Layer 3D Tiles tileset (#1207). Enterprise-only: gates the admin
    /// <c>POST /api/v1/admin/scenes/ingest/citygml</c> surface that parses a
    /// CityGML document and publishes a deterministic tileset with per-feature
    /// discipline / sub-layer semantics. Postgres-only (registration-backed).
    /// </summary>
    public const string SceneBimIngestKey = "scene.bim-ingest";

    /// <summary>
    /// Entitlement key for LAS/LAZ/COPC point-cloud ingest into a servable 3D
    /// Tiles point tileset (#1201). Enterprise-only: gates the admin
    /// <c>POST /api/v1/admin/scenes/ingest/pointcloud</c> surface that decodes a
    /// LAS point cloud and publishes a deterministic <c>.pnts</c> quadtree
    /// tileset preserving per-point classification, intensity, and RGB.
    /// Postgres-only (registration-backed).
    /// </summary>
    public const string ScenePointCloudIngestKey = "scene.pointcloud-ingest";

    /// <summary>
    /// All edition-gated features in the platform.
    /// </summary>
    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        // Alerts — Pro
        new("alerts.enter-exit", "Enter/Exit Geofence Triggers", Categories.Alerts,
            HonuaEdition.Pro, "Trigger alerts when features enter or exit geofence zones."),
        new("alerts.evaluation", "Alert Evaluation Engine", Categories.Alerts,
            HonuaEdition.Pro, "Background worker for evaluating geofence rules against feature changes."),

        // Alerts — Enterprise
        new("alerts.dwell", "Dwell Trigger", Categories.Alerts,
            HonuaEdition.Enterprise, "Trigger alerts when features remain inside a zone for a configured duration."),
        new("alerts.threshold", "Threshold Trigger", Categories.Alerts,
            HonuaEdition.Enterprise, "Trigger alerts based on attribute threshold conditions."),

        // Channels — Pro
        new("channels.webhook", "Webhook Delivery", Categories.Channels,
            HonuaEdition.Pro, "Deliver alert notifications via webhook HTTP callbacks."),

        // Channels — Enterprise
        new("channels.email", "Email Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via email."),
        new("channels.slack", "Slack Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications to Slack channels."),
        new("channels.teams", "Microsoft Teams Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications to Microsoft Teams."),
        new("channels.aws-sns", "AWS SNS Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via AWS Simple Notification Service."),
        new("channels.azure-eventgrid", "Azure Event Grid Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via Azure Event Grid."),
        new("channels.digest", "Digest Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Aggregate and deliver batched alert summaries."),

        // Geocoding — Pro
        new("geocoding.forward", "Forward Geocoding", Categories.Geocoding,
            HonuaEdition.Pro, "Convert addresses to coordinates using configured providers."),
        new("geocoding.reverse", "Reverse Geocoding", Categories.Geocoding,
            HonuaEdition.Pro, "Convert coordinates to addresses using configured providers."),
        new("geocoding.failover", "Provider Failover", Categories.Geocoding,
            HonuaEdition.Pro, "Automatic failover between geocoding providers on error."),

        // Geocoding — Enterprise
        new("geocoding.batch", "Batch Geocoding", Categories.Geocoding,
            HonuaEdition.Enterprise, "Geocode multiple addresses in a single request."),

        // Routing - Pro
        new("routing.solve", "Network Routing", Categories.Routing,
            HonuaEdition.Pro, "Solve multi-stop routes with the configured routing engine (MCP honua_solve_route and future gated surfaces)."),

        // Identity — Community (ArcGIS Portal interop)
        new("identity.portal-token", "ArcGIS Portal Token Issuance", Categories.Identity,
            HonuaEdition.Community, "Expose POST/GET /sharing/rest/generateToken so Esri clients can authenticate against Honua-secured /rest/services."),
        new("identity.portal-sharing", "ArcGIS Portal Sharing Read Surface", Categories.Identity,
            HonuaEdition.Community, "Expose the read-only /sharing/rest Portal facade (info, portals/self, search, content/items) so Esri clients can discover Honua content as portal items."),

        // Identity — Pro (no SSO tax for one provider)
        new(OidcAuthenticationKey, "OIDC Authentication", Categories.Identity,
            HonuaEdition.Pro, "Single-provider OpenID Connect authentication with Azure AD, Google, Okta, Auth0, or generic OIDC."),

        // Identity — Enterprise (multi-provider governance)
        new(OidcMultiProviderKey, "OIDC Multi-Provider SSO", Categories.Identity,
            HonuaEdition.Enterprise, "Configure multiple OIDC identity providers in one deployment."),
        new(OidcClaimsMappingKey, "Claims Mapping", Categories.Identity,
            HonuaEdition.Enterprise, "Custom claim-to-role mapping and identity governance for OIDC providers."),
        new(MtlsClientCertificateKey, "mTLS Client-Certificate Authentication", Categories.Identity,
            HonuaEdition.Enterprise, "Native client-certificate (mTLS) authentication: trust profiles, issuer/chain validation with CRL/OCSP revocation, and certificate-to-principal mapping."),

        // Caching — Pro
        new("caching.output-cache", "Output Caching", Categories.Caching,
            HonuaEdition.Pro, "HTTP response caching with tag-based invalidation."),

        // Caching — Pro
        new("caching.redis", "Redis Distributed Cache", Categories.Caching,
            HonuaEdition.Pro, "Redis-backed distributed cache for multi-node deployments."),

        // Import — Community (one-shot file import ships in Community; see docs/features/README.md)
        new("import.file", "File Import", Categories.Import,
            HonuaEdition.Community, "Import geospatial data from file uploads (GeoJSON, Shapefile, GeoPackage)."),

        // Streaming — Pro
        new("streaming.feature-subscriptions", "Real-Time Feature Streams", Categories.Streaming,
            HonuaEdition.Pro, "Subscribe to WebSocket and SSE feature-change streams with filters and replay cursors."),

        // Field operations — Pro (disconnected/offline sync; online collection remains Community)
        new(FieldOpsOfflineSyncKey, "Offline/Field Sync", Categories.FieldOps,
            HonuaEdition.Pro, "Use disconnected field sync, form offline policy discovery, GeoServices replica/GeoPackage delta sync, and FieldCollection cursor/change exchange."),

        // Import — Enterprise
        new("import.geoservices", "GeoServices Import", Categories.Import,
            HonuaEdition.Enterprise, "Import layers from ArcGIS REST services."),
        new("import.geoserver", "GeoServer Import", Categories.Import,
            HonuaEdition.Enterprise, "Import layers from GeoServer REST API."),

        // Static Map — Pro
        new("staticmap.high-dpi", "High-DPI Static Maps", Categories.StaticMap,
            HonuaEdition.Pro, "Render static map images at 150 and 300 DPI."),
        new("staticmap.large-dimensions", "Large Static Maps", Categories.StaticMap,
            HonuaEdition.Pro, "Render static map images up to 4096x4096 pixels."),
        new("staticmap.rich-overlays", "Rich Static Map Overlays", Categories.StaticMap,
            HonuaEdition.Pro, "Render up to 100 markers and 500 path vertices on static maps."),

        // Styling — Community
        new("styling.defaults", "Smart Style Defaults", Categories.Styling,
            HonuaEdition.Community, "Enhanced geometry-aware default styles for published layers."),
        new("styling.auto-suggest", "Auto-Cartographic Styling", Categories.Styling,
            HonuaEdition.Pro, "Style suggestions based on field analysis and classification."),

        // Raster — Pro (COG import/export are Community-tier and ungated)
        new("raster.cloud-cog-serving", "COG Serving", Categories.Raster,
            HonuaEdition.Pro, "Serve COG files directly from S3/Azure via HTTP range requests."),
        new("raster.cloud-storage-config", "Cloud Storage Configuration", Categories.Raster,
            HonuaEdition.Pro, "Configure cloud storage connections for direct raster serving."),
        new("raster.temporal-mosaic", "Temporal Raster Mosaic", Categories.Raster,
            HonuaEdition.Pro, "Select raster mosaics by acquisition timestamp for time-series imagery."),
        new("raster.multidim-coverage", "Multidimensional Coverage (NetCDF/HDF5/Zarr)", Categories.Raster,
            HonuaEdition.Pro, "Register and serve cloud-hosted NetCDF4/HDF5/Zarr datacubes as multidimensional coverages (OGC API Coverages / WCS / ImageServer multidimensional)."),

        // Analytics — Pro (PostGIS-backed spatial analytics on filtered layer subsets)
        new("analytics.clustering", "Spatial Clustering", Categories.Analytics,
            HonuaEdition.Pro, "Server-side DBSCAN and K-Means clustering with optional cluster hulls."),
        new("analytics.spatial-join", "Spatial Join", Categories.Analytics,
            HonuaEdition.Pro, "Cross-layer spatial join with intersects/contains/within/dwithin predicates."),
        new("analytics.buffer-aggregate", "Buffer Aggregate", Categories.Analytics,
            HonuaEdition.Pro, "Buffer features by a fixed distance and dissolve or aggregate per group."),
        new("analytics.density", "Density Binning", Categories.Analytics,
            HonuaEdition.Pro, "Hex or square grid density (heatmap) binning over a filtered subset."),
        new("analytics.sun-shadow", "Sun/Shadow Analysis", Categories.Analytics,
            HonuaEdition.Pro, "Compute solar position from date/time/location and cast the shadow extent against the elevation surface."),
        new("analytics.slice", "Slice/Volumetric Analysis", Categories.Analytics,
            HonuaEdition.Pro, "Intersect a vertical slice plane with the elevation surface and return cross-section metadata."),
        new("analytics.line-of-sight", "Line of Sight", Categories.Analytics,
            HonuaEdition.Pro, "Determine terrain visibility between an observer and target over the elevation surface."),
        new("analytics.viewshed", "Viewshed", Categories.Analytics,
            HonuaEdition.Pro, "Compute the radially-sampled visible area around an observer over the elevation surface."),

        // Temporal — Community (basic discovery)
        new("temporal.filtering", "Temporal Query Filtering", Categories.Temporal,
            HonuaEdition.Community, "Filter feature queries by a bounded or open-ended time range."),
        new("temporal.extent-discovery", "Temporal Extent Discovery", Categories.Temporal,
            HonuaEdition.Community, "Expose timeInfo, dedicated extent endpoint, and OGC time dimension metadata for time-aware layers."),

        // Temporal — Pro (animation, playback, time-series tiles)
        new("temporal.histogram", "Temporal Histogram (Date Bins)", Categories.Temporal,
            HonuaEdition.Pro, "Count features per time bucket using calendar or fixed bins for animation frame planning."),
        new("temporal.time-series-tiles", "Time-Series Tile Filtering", Categories.Temporal,
            HonuaEdition.Pro, "Filter vector tile requests to a bounded time range via the time parameter."),
        new("temporal.animation-api", "Animation API Contract", Categories.Temporal,
            HonuaEdition.Pro, "Capability flag for SDK/admin TimeSlider and playback integration."),

        // Printing — Pro
        new("printing.pdf-output", "PDF Print Output", Categories.Printing,
            HonuaEdition.Pro, "Export print jobs as PDF files."),
        new("printing.layout-templates", "Print Layout Templates", Categories.Printing,
            HonuaEdition.Pro, "Use full print layout templates beyond MAP_ONLY."),

        // Editing — Pro (Esri GeoServices FeatureServer write surface only; open-protocol
        // editing via OGC API Features, WFS-T, OData, and gRPC is Community and ungated)
        new(FeatureServerEditsKey, "FeatureServer Editing", Categories.Editing,
            HonuaEdition.Pro, "Create, update, and delete features through the Esri GeoServices FeatureServer write surface — applyEdits, addFeatures, updateFeatures, deleteFeatures, and calculate."),

        // Editing — Enterprise (Esri-style branch versioning; Postgres-only)
        new(BranchVersioningKey, "Branch Versioning", Categories.Editing,
            HonuaEdition.Enterprise, "Named gdb versions with isolated edits, reconcile/post back to DEFAULT, and gdbVersion-scoped editing/querying over the GeoServices VersionManagementServer."),

        // AI operations — Pro (read/discovery/query surfaces remain Community)
        new(AiSpecApplyKey, "Spec Apply Execution", Categories.Ai,
            HonuaEdition.Pro, "Apply executable specs and submit MCP plan execution jobs from agentic tooling."),
        new(AiGroundingKey, "Spec Grounding Mutations", Categories.Ai,
            HonuaEdition.Pro, "Ground natural-language turns into validated spec mutation plans."),
        new(AiWorkflowGenerationKey, "AI Workflow and Content Generation", Categories.Ai,
            HonuaEdition.Pro, "Generate or refine workflows, maps, apps, forms, reports, dashboards, saved queries, and analysis packages from natural-language prompts."),

        // Scene — Enterprise (CityGML/BIM ingest + Building Scene Layer publishing)
        new(SceneBimIngestKey, "CityGML/BIM Scene Ingest", Categories.Scene,
            HonuaEdition.Enterprise, "Ingest CityGML building models into a servable Building Scene Layer 3D Tiles tileset with per-feature discipline / sub-layer semantics."),
        new(ScenePointCloudIngestKey, "Point Cloud Scene Ingest", Categories.Scene,
            HonuaEdition.Enterprise, "Ingest LAS point clouds into a servable 3D Tiles point tileset preserving per-point classification, intensity, and RGB."),

        // Extensibility — Enterprise (plugin/extension SDK)
        new(PluginSdkKey, "Plugin/Extension SDK", Categories.Extensibility,
            HonuaEdition.Enterprise, "Register compile-time, AOT-safe server plugins (custom feature validators and pre/post-edit hooks) discovered and gated at startup."),

        // AI — Community (MCP discovery/query stays free to keep agent reads ungated)
        new(McpDiscoveryKey, "MCP Discovery & Query", Categories.Ai,
            HonuaEdition.Community, "Discover, search, and query Honua through MCP and the agent read surface without a license."),

        // AI — Pro (validation layer for agent-initiated change)
        new(AiOperationsKey, "Agent Operations (Validation Layer)", Categories.Ai,
            HonuaEdition.Pro, "Agent-initiated changes run through a validation layer — planned, dry-run, validated, with a pre-change snapshot/backup gate before apply (spec apply, NL grounding mutations, workflow generation, and MCP execute tools)."),

        // AI — Enterprise (approval + policy on top of the validation layer)
        new(AiApprovalWorkflowsKey, "Agent Approval Workflows", Categories.Ai,
            HonuaEdition.Enterprise, "Human-in-the-loop approval, policy-scoped agent permissions, and immutable audit for agent-initiated operations."),

        // Disaster Recovery — Enterprise (HA/DR: backup automation, failover, RTO/RPO reporting)
        new(BackupAutomationKey, "Backup Automation", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Scheduled PostgreSQL base backups plus WAL archiving enabling point-in-time recovery."),
        new(FailoverPlaybooksKey, "Failover Playbooks", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Active-passive failover playbooks driven by automated health checks against the primary serving surface."),
        new(CacheBackupKey, "Cache State Backup", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Backup and restore Redis cache state so warm cache contents survive a regional failover."),
        new(RecoveryReportingKey, "RTO/RPO Reporting", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Track recovery time and recovery point objectives and report recovery readiness, last successful backup, and restorable point."),
    ];
}
