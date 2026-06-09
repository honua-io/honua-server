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

        /// <summary>Temporal animation, time-aware filtering, and time-series tile features.</summary>
        public const string Temporal = "Temporal";

        /// <summary>Printing and layout export features.</summary>
        public const string Printing = "Printing";

        /// <summary>Editing features — branch versioning, reconcile/post, multi-user editing.</summary>
        public const string Editing = "Editing";

        /// <summary>Server extensibility features — plugin/extension SDK.</summary>
        public const string Extensibility = "Extensibility";
    }

    /// <summary>
    /// Entitlement key for Esri-style branch versioning (named gdb versions, version read/edit
    /// sessions, reconcile/post). Gates the GeoServices VersionManagementServer surface and
    /// <c>gdbVersion</c>-scoped FeatureServer editing (#1272, ADR-0051). Postgres-only.
    /// </summary>
    public const string BranchVersioningKey = "editing.branch-versioning";

    /// <summary>
    /// Entitlement key for the server plugin/extension SDK (custom feature validators and edit
    /// hooks today; computed fields and custom endpoints in later phases). Gates compile-time,
    /// AOT-safe plugins registered at startup (#347, ADR-0024). Enterprise-only.
    /// </summary>
    public const string PluginSdkKey = "plugin.sdk";

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

        // Identity — Community (ArcGIS Portal interop)
        new("identity.portal-token", "ArcGIS Portal Token Issuance", Categories.Identity,
            HonuaEdition.Community, "Expose POST/GET /sharing/rest/generateToken so Esri clients can authenticate against Honua-secured /rest/services."),
        new("identity.portal-sharing", "ArcGIS Portal Sharing Read Surface", Categories.Identity,
            HonuaEdition.Community, "Expose the read-only /sharing/rest Portal facade (info, portals/self, search, content/items) so Esri clients can discover Honua content as portal items."),

        // Identity — Enterprise
        new("identity.oidc", "OIDC Authentication", Categories.Identity,
            HonuaEdition.Enterprise, "OpenID Connect multi-provider authentication with Azure AD, Google, and generic OIDC."),
        new("identity.claims-mapping", "Claims Mapping", Categories.Identity,
            HonuaEdition.Enterprise, "Custom claim-to-role mapping for OIDC providers."),

        // Caching — Pro
        new("caching.output-cache", "Output Caching", Categories.Caching,
            HonuaEdition.Pro, "HTTP response caching with tag-based invalidation."),

        // Caching — Pro
        new("caching.redis", "Redis Distributed Cache", Categories.Caching,
            HonuaEdition.Pro, "Redis-backed distributed cache for multi-node deployments."),

        // Import — Pro
        new("import.file", "File Import", Categories.Import,
            HonuaEdition.Pro, "Import geospatial data from file uploads (GeoJSON, Shapefile, GeoPackage)."),

        // Streaming — Pro
        new("streaming.feature-subscriptions", "Real-Time Feature Streams", Categories.Streaming,
            HonuaEdition.Pro, "Subscribe to WebSocket and SSE feature-change streams with filters and replay cursors."),

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

        // Editing — Enterprise (Esri-style branch versioning; Postgres-only)
        new(BranchVersioningKey, "Branch Versioning", Categories.Editing,
            HonuaEdition.Enterprise, "Named gdb versions with isolated edits, reconcile/post back to DEFAULT, and gdbVersion-scoped editing/querying over the GeoServices VersionManagementServer."),

        // Extensibility — Enterprise (plugin/extension SDK)
        new(PluginSdkKey, "Plugin/Extension SDK", Categories.Extensibility,
            HonuaEdition.Enterprise, "Register compile-time, AOT-safe server plugins (custom feature validators and pre/post-edit hooks) discovered and gated at startup."),
    ];
}
