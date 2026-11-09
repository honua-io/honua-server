// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public sealed record FeatureDefinition(
    string Key,
    string DisplayName,
    string Category,
    HonuaEdition MinimumEdition,
    string Description);

public static class FeatureCatalog
{
    public static class Categories
    {
        public const string Alerts = "Alerts";

        public const string Channels = "Channels";

        public const string Import = "Import";

        public const string Geocoding = "Geocoding";

        public const string Routing = "Routing";

        public const string Identity = "Identity";

        public const string Caching = "Caching";

        public const string StaticMap = "StaticMap";

        public const string Styling = "Styling";

        public const string Raster = "Raster";

        public const string Analytics = "Analytics";

        public const string Streaming = "Streaming";

        public const string Scene = "Scene";

        public const string Temporal = "Temporal";

        public const string Printing = "Printing";

        public const string Editing = "Editing";

        public const string FieldOps = "FieldOps";

        public const string Ai = "AI";

        public const string Extensibility = "Extensibility";

        public const string DisasterRecovery = "DisasterRecovery";

        public const string ControlPlane = "ControlPlane";

        // Hosted-only capabilities are not a preview and have no customer GA path, because the Fair Core License prohibits offering Honua as a hosted service.
        public const string Internal = "Internal";
    }

    public const string MultiTenancyKey = "admin.multi-tenancy";

    public const string BackupAutomationKey = "dr.backup-automation";

    public const string FailoverPlaybooksKey = "dr.failover";

    public const string CacheBackupKey = "dr.cache-backup";

    public const string RecoveryReportingKey = "dr.rto-rpo-reporting";

    public const string AiOperationsKey = "ai.agent-operations";

    public const string AiApprovalWorkflowsKey = "ai.approval-workflows";

    public const string McpDiscoveryKey = "ai.mcp-discovery";

    public const string BranchVersioningKey = "editing.branch-versioning";

    // Pro entitlement for FeatureServer writes. Open-protocol edits stay Community and ungated.
    // Branch versioning is a separate entitlement.
    public const string FeatureServerEditsKey = "editing.featureserver-edits";

    public const string FieldOpsOfflineSyncKey = "fieldops.offline-sync";

    public const string AiSpecApplyKey = "ai.spec-apply";

    public const string AiGroundingKey = "ai.grounding";

    public const string PluginSdkKey = "plugin.sdk";

    public const string BatchGeocodingKey = "geocoding.batch";

    public const string GeocodingFailoverKey = "geocoding.failover";

    // The OIDC admin configuration surface is edition-gated; the JWT bearer pipeline is not, so existing sessions keep validating.
    public const string OidcAuthenticationKey = "identity.oidc";

    public const string AlertsEnterExitKey = "alerts.enter-exit";

    public const string AlertsEvaluationKey = "alerts.evaluation";

    public const string AlertsDwellKey = "alerts.dwell";

    public const string AlertsThresholdKey = "alerts.threshold";

    public const string ChannelsWebhookKey = "channels.webhook";

    public const string ChannelsEmailKey = "channels.email";

    public const string ChannelsSlackKey = "channels.slack";

    public const string ChannelsTeamsKey = "channels.teams";

    public const string ChannelsAwsSnsKey = "channels.aws-sns";

    public const string ChannelsAzureEventGridKey = "channels.azure-eventgrid";

    public const string ChannelsDigestKey = "channels.digest";

    public const string OutputCacheKey = "caching.output-cache";

    public const string RedisCacheKey = "caching.redis";

    public const string SamlAuthenticationKey = "identity.saml";

    public const string ScimProvisioningKey = "identity.scim";

    public const string OidcMultiProviderKey = "identity.oidc-multi-provider";

    public const string OidcClaimsMappingKey = "identity.claims-mapping";

    public const string MtlsClientCertificateKey = "identity.mtls-client-certificate";

    public const string SceneBimIngestKey = "scene.bim-ingest";

    public const string ScenePointCloudIngestKey = "scene.pointcloud-ingest";

    public const string H3AnalyticsKey = "analytics.h3";

    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        // Alerts — Pro
        new(AlertsEnterExitKey, "Enter/Exit Geofence Triggers", Categories.Alerts,
            HonuaEdition.Pro, "Trigger alerts when features enter or exit geofence zones."),
        new(AlertsEvaluationKey, "Alert Evaluation Engine", Categories.Alerts,
            HonuaEdition.Pro, "Background worker for evaluating geofence rules against feature changes."),

        // Alerts — Enterprise
        new(AlertsDwellKey, "Dwell Trigger", Categories.Alerts,
            HonuaEdition.Enterprise, "Trigger alerts when features remain inside a zone for a configured duration."),
        new(AlertsThresholdKey, "Threshold Trigger", Categories.Alerts,
            HonuaEdition.Enterprise, "Trigger alerts based on attribute threshold conditions."),

        // Channels — Pro
        new(ChannelsWebhookKey, "Webhook Delivery", Categories.Channels,
            HonuaEdition.Pro, "Deliver alert notifications via webhook HTTP callbacks."),

        // Channels — Enterprise
        new(ChannelsEmailKey, "Email Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via email."),
        new(ChannelsSlackKey, "Slack Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications to Slack channels."),
        new(ChannelsTeamsKey, "Microsoft Teams Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications to Microsoft Teams."),
        new(ChannelsAwsSnsKey, "AWS SNS Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via AWS Simple Notification Service."),
        new(ChannelsAzureEventGridKey, "Azure Event Grid Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Deliver alert notifications via Azure Event Grid."),
        new(ChannelsDigestKey, "Digest Delivery", Categories.Channels,
            HonuaEdition.Enterprise, "Aggregate and deliver batched alert summaries."),

        // Geocoding — Pro
        new(GeocodingFailoverKey, "Provider Failover", Categories.Geocoding,
            HonuaEdition.Pro, "Automatic failover between geocoding providers on error."),

        // Geocoding — Enterprise. geocoding.forward and geocoding.reverse moved to
        // CapabilityKeyCatalog.CommunityKeys (#2981) — the demo/adoption showcase path is
        // Community; batch geocoding is the volume/enterprise workload that stays gated. Both
        // shipped Pro in the catalog but unenforced over HTTP before this change (only the MCP
        // honua_geocode_address tool checked geocoding.forward); see ADR-0024.
        new(BatchGeocodingKey, "Batch Geocoding", Categories.Geocoding,
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
        new(SamlAuthenticationKey, "SAML 2.0 Authentication", Categories.Identity,
            HonuaEdition.Enterprise, "SAML 2.0 service-provider metadata, assertion consumer, and single-logout endpoints."),
        new(ScimProvisioningKey, "SCIM 2.0 Provisioning", Categories.Identity,
            HonuaEdition.Enterprise, "User/group provisioning through the SCIM 2.0 protocol surface."),

        // Caching — Pro
        new(OutputCacheKey, "Output Caching", Categories.Caching,
            HonuaEdition.Pro, "HTTP response caching with tag-based invalidation."),

        // Caching — Pro
        new(RedisCacheKey, "Redis Distributed Cache", Categories.Caching,
            HonuaEdition.Pro, "Redis-backed distributed cache for multi-node deployments."),

        // Import — Community (one-shot file import ships in Community; see docs/features/README.md)
        new("import.file", "File Import", Categories.Import,
            HonuaEdition.Community, "Import geospatial data from file uploads (GeoJSON, Shapefile, GeoPackage)."),

        // Streaming — Pro
        new("streaming.feature-subscriptions", "Real-Time Feature Streams", Categories.Streaming,
            HonuaEdition.Pro, "Subscribe to WebSocket and SSE feature-change streams with filters and replay cursors."),

        // Field operations — Pro (disconnected/offline sync; online collection remains Community)
        new(FieldOpsOfflineSyncKey, "Offline/Field Sync", Categories.FieldOps,
            HonuaEdition.Pro, "Use disconnected field sync, form offline policy discovery, GeoServices replica delta sync delivered as Esri JSON, and FieldCollection cursor/change exchange."),

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
        new(H3AnalyticsKey, "H3 Aggregation", Categories.Analytics,
            HonuaEdition.Pro, "Aggregate features into H3 cells and publish H3 vector tiles."),
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

        // Editing — Pro (core branch versioning; organisational approval/policy remains Enterprise)
        new(BranchVersioningKey, "Branch Versioning", Categories.Editing,
            HonuaEdition.Pro, "Core branch versioning (Pro): named gdb versions, isolated edits, reconcile/post, and gdbVersion-scoped editing/querying. Organisational approval and policy controls remain Enterprise. Preview in 2026.1; the experimental runtime opt-in remains required."),

        // AI operations — Pro (read/discovery/query surfaces remain Community)
        new(AiSpecApplyKey, "Spec Apply Execution", Categories.Ai,
            HonuaEdition.Pro, "Apply executable specs and submit MCP plan execution jobs from agentic tooling."),
        new(AiGroundingKey, "Spec Grounding Mutations", Categories.Ai,
            HonuaEdition.Pro, "Ground natural-language turns into validated spec mutation plans."),

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
            HonuaEdition.Enterprise, "Enterprise organisational approval workflows and policy-scoped agent permissions. Essential authorization, isolation, and audit integrity remain baseline product obligations."),

        // Disaster Recovery — Enterprise (HA/DR: backup automation, failover, RTO/RPO reporting)
        new(BackupAutomationKey, "Backup Automation", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Enterprise scheduling and orchestration of PostgreSQL base backups and WAL archiving. Essential backup, restore, and recoverability remain baseline obligations in every edition."),
        new(FailoverPlaybooksKey, "Failover Playbooks", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Enterprise automated active-passive failover playbooks driven by health checks. Essential recovery procedures remain baseline obligations in every edition."),
        new(CacheBackupKey, "Cache State Backup", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Enterprise automation for Redis warm-cache recovery during regional failover. Recovery of enabled durable Redis job/workflow state remains a baseline obligation in every edition."),
        new(RecoveryReportingKey, "RTO/RPO Reporting", Categories.DisasterRecovery,
            HonuaEdition.Enterprise, "Enterprise automated RTO/RPO reporting and recovery-readiness dashboards. Documented recovery expectations and verified restores remain baseline obligations in every edition."),

        // Internal — Honua's own hosted operation; not offered to licensees
        new(MultiTenancyKey, "Multi-Tenant Operation", Categories.Internal,
            HonuaEdition.Enterprise, "Internal to Honua's own hosted operation; not part of the customer-deployable surface and not offered to licensees. Honua deployments are single-tenant. This has no path to general availability as a customer capability: the Fair Core License prohibits providing Honua to third parties as a hosted or managed service, so a licensee can never be the party operating it. Cross-tenant disclosure remains a full-severity security defect regardless."),
    ];
}
