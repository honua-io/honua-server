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
    ];
}
