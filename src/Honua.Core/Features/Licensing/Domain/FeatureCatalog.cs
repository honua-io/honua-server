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
    }

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

        // Identity — Enterprise
        new("identity.oidc", "OIDC Authentication", Categories.Identity,
            HonuaEdition.Enterprise, "OpenID Connect multi-provider authentication with Azure AD, Google, and generic OIDC."),
        new("identity.claims-mapping", "Claims Mapping", Categories.Identity,
            HonuaEdition.Enterprise, "Custom claim-to-role mapping for OIDC providers."),

        // Caching — Pro
        new("caching.output-cache", "Output Caching", Categories.Caching,
            HonuaEdition.Pro, "HTTP response caching with tag-based invalidation."),

        // Caching — Enterprise
        new("caching.redis", "Redis Distributed Cache", Categories.Caching,
            HonuaEdition.Enterprise, "Redis-backed distributed cache for multi-node deployments."),

        // Import — Pro
        new("import.file", "File Import", Categories.Import,
            HonuaEdition.Pro, "Import geospatial data from file uploads (GeoJSON, Shapefile, GeoPackage)."),

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

        // Analytics — Pro (PostGIS-backed spatial analytics on filtered layer subsets)
        new("analytics.clustering", "Spatial Clustering", Categories.Analytics,
            HonuaEdition.Pro, "Server-side DBSCAN and K-Means clustering with optional cluster hulls."),
        new("analytics.spatial-join", "Spatial Join", Categories.Analytics,
            HonuaEdition.Pro, "Cross-layer spatial join with intersects/contains/within/dwithin predicates."),
        new("analytics.buffer-aggregate", "Buffer Aggregate", Categories.Analytics,
            HonuaEdition.Pro, "Buffer features by a fixed distance and dissolve or aggregate per group."),
        new("analytics.density", "Density Binning", Categories.Analytics,
            HonuaEdition.Pro, "Hex or square grid density (heatmap) binning over a filtered subset."),
    ];
}
