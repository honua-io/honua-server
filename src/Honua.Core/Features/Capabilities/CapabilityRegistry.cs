// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The default <see cref="ICapabilityRegistry"/> — the single source of truth
/// for the platform capability roster (ADR-0058, Decision B). B1 populates it to
/// <b>faithfully mirror the live roster that exists today</b>: the <c>/mcp</c>
/// tools and resources (from <c>McpOperatorSurface</c> /
/// <c>McpServiceCollectionExtensions</c>), the #1186 capability-manifest
/// capabilities (from <c>CapabilityManifestService</c>), and the platform data
/// formats (from <c>SupportedFileFormat</c> and the shared format readers/writers).
/// It is a description-layer projection only — B1 disables nothing and changes no
/// behaviour. B2 (#2334) composes <c>/mcp</c> from this registry and B3 (#2335)
/// layers the #1186 per-environment resolver onto it.
/// </summary>
/// <remarks>
/// A runtime registry↔catalog conformance check pins this roster to the live
/// surface so it cannot silently drift (see the conformance test that mirrors the
/// <c>McpTaxonomyAlignmentTests</c> / <c>FeatureCatalogDriftTests</c> style).
/// </remarks>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    /// <summary>Id prefix for <c>/mcp</c> tool capability descriptors.</summary>
    public const string McpToolIdPrefix = "protocol.mcp.tool.";

    /// <summary>Id prefix for <c>/mcp</c> resource-family capability descriptors.</summary>
    public const string McpResourceIdPrefix = "protocol.mcp.resource.";

    /// <summary>Id prefix for data-format capability descriptors.</summary>
    public const string DataFormatIdPrefix = "format.";

    /// <summary>Reason code returned by <see cref="Resolve"/> for an unregistered id.</summary>
    public const string NotRegisteredReasonCode = CapabilityReasonCodes.NotRegistered;

    private static readonly Dictionary<string, HonuaEdition> EditionByEntitlementKey =
        FeatureCatalog.All.ToDictionary(
            static f => f.Key,
            static f => f.MinimumEdition,
            StringComparer.Ordinal);

    private static readonly IReadOnlyList<CapabilityDescriptor> AllDescriptors = BuildAll();

    private static readonly Dictionary<string, CapabilityDescriptor> ById =
        AllDescriptors.ToDictionary(static d => d.Id, StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDescriptor> All => AllDescriptors;

    /// <inheritdoc />
    public CapabilityDescriptor? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ById.GetValueOrDefault(id);
    }

    /// <inheritdoc />
    public CapabilityResolution Resolve(string id, CapabilityGateContext context)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(context);

        // #2339 (T2): the config-driven experimental feature-flag precedence. The
        // registry supplies the descriptor (or null for an unknown id) and defers the
        // maturity/flag/edition precedence to CapabilityGateResolver.
        return CapabilityGateResolver.Resolve(Find(id), context);
    }

    private static List<CapabilityDescriptor> BuildAll()
    {
        var descriptors = new List<CapabilityDescriptor>();
        descriptors.AddRange(BuildMcpToolDescriptors());
        descriptors.AddRange(BuildMcpResourceDescriptors());
        descriptors.AddRange(BuildManifestCapabilityDescriptors());
        descriptors.AddRange(BuildDataFormatDescriptors());
        return descriptors;
    }

    // -----------------------------------------------------------------------
    // /mcp tools — the live tools/list roster (McpServiceCollectionExtensions).
    // (advertisedName, geospatial-mcp standardName, workflow family)
    // -----------------------------------------------------------------------
    private static IEnumerable<CapabilityDescriptor> BuildMcpToolDescriptors()
    {
        (string Advertised, string Standard, string Family)[] tools =
        [
            ("honua_plan_analysis", "plan_analysis", "planning"),
            ("honua_ground_candidates", "ground_candidates", "planning"),
            ("honua_clarify_intent", "clarify_intent", "planning"),
            ("honua_validate_plan", "validate_plan", "planning"),
            ("honua_dry_run_plan", "validate_plan", "planning"),
            ("honua_validate_package", "validate_package", "planning"),
            ("honua_preview_package", "preview_package", "planning"),
            ("honua_execute_plan", "execute_plan", "execution"),
            ("honua_create_map_package", "create_map_package", "execution"),
            ("honua_create_app_package", "create_app_package", "execution"),
            ("honua_geocode_address", "geocode_address", "execution"),
            ("honua_geocode_addresses", "geocode_addresses", "execution"),
            ("honua_solve_route", "solve_route", "execution"),
            ("honua_cancel_job", "cancel_job", "lifecycle"),
            ("honua_propose_operation", "propose_operation", "lifecycle"),
            ("honua_ingest_dataset", "ingest_dataset", "lifecycle"),
            ("honua_publish_service", "publish_service", "lifecycle"),
            ("honua_publish_result", "publish_result", "lifecycle"),
            ("honua_ops_health", "ops_health", "results"),
            ("honua_ops_findings", "ops_findings", "results"),
            ("honua_alert_events", "alert_events", "results"),
            ("honua_operate_events", "operate_events", "results"),
            ("honua_platform_release_status", "platform_release_status", "results"),
            ("honua_deploy_operations", "deploy_operations", "results"),
            ("honua_propose_rollback", "propose_rollback", "lifecycle"),
            ("honua_list_layers", "list_layers", "results"),
            ("honua_query_features", "query_features", "results"),
            // No honua_edit_features: Honua does not expose an AI/MCP feature-mutation
            // tool per ADR-0028 (AI operational data editing is not supported;
            // founder-reaffirmed 2026-07-06). The shared edit pipeline serves the
            // human-facing HTTP adapters only.
            ("honua_render_map", "render_map", "results"),
            ("honua_get_style", "get_style", "results"),
            ("honua_apply_style_preset", "apply_style_preset", "execution"),
            ("honua_resolve_entity", "resolve_entity", "results"),
            ("honua_list_capabilities", "list_capabilities", "results"),
        ];

        foreach (var (advertised, standard, family) in tools)
        {
            yield return new CapabilityDescriptor
            {
                Id = McpToolIdPrefix + advertised[McpToolNamePrefixLength..],
                Category = family,
                Kind = CapabilityKind.ProtocolOperation,
                Maturity = CapabilityMaturity.Implemented,
                StandardName = standard,
                McpToolName = advertised,
                ConformanceMapping = "tools",
            };
        }
    }

    private const int McpToolNamePrefixLength = 6; // "honua_".Length

    // -----------------------------------------------------------------------
    // /mcp resource families — the live resources/list + resources/templates/list
    // roster (default composition + opt-in promotion surface).
    // (family, standard label, honua:// URI templates)
    // -----------------------------------------------------------------------
    private static IEnumerable<CapabilityDescriptor> BuildMcpResourceDescriptors()
    {
        (string Family, string StandardLabel, string[] Uris)[] resources =
        [
            ("jobs", "Job", ["honua://jobs/{jobId}"]),
            ("job-results", "Job results", ["honua://jobs/{jobId}/results"]),
            ("job-reports", "Job report", ["honua://jobs/{jobId}/report"]),
            ("proposals", "Proposal", ["honua://proposals/{proposalId}"]),
            ("workspaces", "Workspace", ["honua://workspaces/{workspaceId}"]),
            ("catalog", "Catalog", ["honua://catalog/processes", "honua://catalog/features"]),
            ("ops-health", "Ops health", ["honua://ops/health"]),
            ("ops-findings", "Ops findings", ["honua://ops/findings"]),
            ("published-services", "Published service", ["honua://published-services/{serviceId}"]),
            ("deployments", "Deployment", ["honua://deployments/{deploymentId}"]),
            ("map-packages", "Map package", ["honua://map-packages/{packageId}"]),
            ("app-packages", "App package", ["honua://app-packages/{packageId}"]),
            (
                "promotion-index",
                "Promotion index",
                [
                    "honua://published-services",
                    "honua://deployments",
                    "honua://map-packages",
                    "honua://app-packages",
                ]),
        ];

        foreach (var (family, standardLabel, uris) in resources)
        {
            yield return new CapabilityDescriptor
            {
                Id = McpResourceIdPrefix + family,
                Category = "resource",
                Kind = CapabilityKind.ProtocolOperation,
                Maturity = CapabilityMaturity.Implemented,
                StandardName = standardLabel,
                ResourceUriTemplates = uris,
                ConformanceMapping = "resources",
            };
        }
    }

    // -----------------------------------------------------------------------
    // #1186 capability-manifest capabilities — mirrors
    // CapabilityManifestService.BuildCapabilities (honua.capability_manifest.v1).
    // (id, category, entitlement key, kind)
    // -----------------------------------------------------------------------
    private static IEnumerable<CapabilityDescriptor> BuildManifestCapabilityDescriptors()
    {
        // PackageSchemaVersion is populated only for the `packages`-category
        // capabilities (#2335 / B3): it is the package-family schema version the
        // honua.capability_manifest.v1 wire advertises for that family, and the null
        // seam B1 left. It mirrors CapabilityManifestService's package-family list so
        // the derived Packages.Families[] and the descriptors cannot diverge.
        // T10 (#2346): the built-experimental capabilities are flipped from
        // Implemented to Experimental here. They are implemented server-side but
        // gated OFF the first-release surface (ADR-0058): with the default config
        // (Capabilities:Experimental:Enabled=false and no per-capability override)
        // the T2 resolver reports them experimental-disabled, so the registry-derived
        // manifest omits them (B3), the T5 endpoint gates 404 their route groups, and
        // the feature catalog projects them as `experimental`. A customer opts in per
        // capability via Capabilities:Experimental:{id}:Enabled=true. Concepts whose
        // built-experimental surface has no distinct registry descriptor (SIEM/investigations,
        // cross-environment promotion, rollback/auto-rollback, collaborative map sessions,
        // governance auth SSO/OIDC/SAML/SCIM, forms/form-packages, mobile field-collection
        // beyond offline sync) are intentionally left as-is — there is no descriptor to flip.
        // versioning.branch (VMS) is now gated here via its own descriptor (added below).
        (string Id, string Category, string? EntitlementKey, CapabilityKind Kind, string? PackageSchemaVersion, CapabilityMaturity Maturity)[] capabilities =
        [
            ("package.metadata-v2", "packages", null, CapabilityKind.Feature, MetadataV2Constants.SchemaVersion, CapabilityMaturity.Implemented),
            ("package.release-package", "packages", null, CapabilityKind.Feature, MetadataV2Constants.ApiVersion, CapabilityMaturity.Implemented),
            ("package.gitops-manifest", "packages", null, CapabilityKind.Feature, MetadataV2Constants.ApiVersion, CapabilityMaturity.Implemented),
            ("package.map", "packages", null, CapabilityKind.Feature, MapPackageSchemaVersion, CapabilityMaturity.Implemented),
            ("package.app", "packages", null, CapabilityKind.Feature, AppPackageSchemaVersion, CapabilityMaturity.Implemented),

            // Temporal analytics — promoted to GA (Implemented) in #2429. Time filtering
            // (Community) + extent discovery (Community), date-bin histograms (Pro),
            // time-series tiles (Pro). The registry maturity flip un-gates the manifest
            // omission and the /api/v1/temporal/* route gate; edition entitlements
            // (Community vs Pro, FeatureCatalog) still apply — GA does not bypass licensing.
            // Provider support is Postgres (all four) + DuckDB (filtering/extent/histogram);
            // providers without temporal SQL fail loud (NotSupportedException), never silently
            // return unfiltered rows — see the per-provider guards hardened in #2429.
            ("temporal.filtering", "temporal", "temporal.filtering", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("temporal.extent-discovery", "temporal", "temporal.extent-discovery", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("temporal.histogram", "temporal", "temporal.histogram", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("temporal.time-series-tiles", "temporal", "temporal.time-series-tiles", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),

            // Disconnected-sync conflict review — built-experimental (T10 flip).
            ("sync.offline", "sync", FeatureCatalog.FieldOpsOfflineSyncKey, CapabilityKind.Feature, null, CapabilityMaturity.Experimental),
            // Realtime feature streaming — promoted to GA (Implemented) in #2428.
            // Second Experimental->Implemented promotion (after alerts.geofence, #2427):
            // WebSocket/SSE feature-change streams with subscription filters and durable
            // replay cursors ship on the default first-release surface. Still Pro-edition
            // gated (streaming.feature-subscriptions entitlement) — GA means no longer
            // hidden/unadvertised, not free-tier.
            ("realtime.feature-streams", "realtime", "streaming.feature-subscriptions", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            // Geofence enter/exit alerting — promoted to GA (Implemented) in #2427.
            // First Experimental->Implemented promotion; engine ships as shared,
            // un-gated GA infrastructure. Workers still self-gate on
            // AlertOptions.Enabled (default false); GA does not mean on-by-default.
            ("alerts.geofence", "alerts", "alerts.enter-exit", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("jobs.runner", "jobs", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("ai.spec-apply", "ai", FeatureCatalog.AiSpecApplyKey, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("ai.grounding", "ai", FeatureCatalog.AiGroundingKey, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("ai.workflow-generation", "ai", FeatureCatalog.AiWorkflowGenerationKey, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("gitops.release-manifest", "gitops", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),

            ("transport.grpc", "transports", null, CapabilityKind.ProtocolOperation, null, CapabilityMaturity.Implemented),
            ("transport.grpc-web", "transports", null, CapabilityKind.ProtocolOperation, null, CapabilityMaturity.Implemented),
            ("transport.native-grpc", "transports", null, CapabilityKind.ProtocolOperation, null, CapabilityMaturity.Implemented),
            ("transport.mcp", "transports", null, CapabilityKind.ProtocolOperation, null, CapabilityMaturity.Implemented),
            ("transport.qgis", "transports", null, CapabilityKind.ProtocolOperation, null, CapabilityMaturity.Implemented),

            // Native mTLS (client-certificate) authentication — promoted to GA (Implemented) in
            // #2431. Second Experimental->Implemented promotion (after alerts.geofence, #2427):
            // hardened chain/CRL-OCSP revocation validation, then flipped off the experimental
            // gate. Enterprise entitlement (FeatureCatalog.MtlsClientCertificateKey) so the
            // manifest/registry advertise it as Enterprise-gated.
            ("security.mtls", "security", FeatureCatalog.MtlsClientCertificateKey, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("preview.file-import", "preview", "import.file", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("query.features", "query", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            // analysis.spatial is gated by a composite of four analytics keys; the
            // single-key EntitlementKey field cannot represent it, so it is left
            // null (the manifest projection keeps the composite key set in B3).
            ("analysis.spatial", "analysis", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("publication.metadata-release", "publication", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("upload.file", "upload", "import.file", CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            ("edit.features", "edit", FeatureCatalog.FeatureServerEditsKey, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
            // Branch versioning (VMS REST surface) — built-experimental (ADR-0058 / BH6-001/BH6-002 fix).
            // The VMS endpoints are gated OFF the GA surface by default (versioning.branch descriptor).
            // Opt in via Capabilities:Experimental:versioning.branch:Enabled=true.
            ("versioning.branch", "versioning", FeatureCatalog.BranchVersioningKey, CapabilityKind.Feature, null, CapabilityMaturity.Experimental),

            // Aggregated operational status (A12): the server-authoritative operate/status surface —
            // one server-computed verdict + per-domain rollups + SLO/error-budget contract. Ungated GA.
            ("operate.status", "operate", null, CapabilityKind.Feature, null, CapabilityMaturity.Implemented),
        ];

        foreach (var (id, category, entitlementKey, kind, packageSchemaVersion, maturity) in capabilities)
        {
            yield return new CapabilityDescriptor
            {
                Id = id,
                Category = category,
                Kind = kind,
                Maturity = maturity,
                EntitlementKey = entitlementKey,
                MinimumEdition = ResolveMinimumEdition(entitlementKey),
                PackageSchemaVersion = packageSchemaVersion,
            };
        }
    }

    /// <summary>The map-package family schema version advertised by the manifest wire.</summary>
    private const string MapPackageSchemaVersion = "honua_map_package.v1";

    /// <summary>The app-package family schema version advertised by the manifest wire.</summary>
    private const string AppPackageSchemaVersion = "honua_app_package.v1";

    // -----------------------------------------------------------------------
    // Data formats — the shared format readers/writers. The import formats mirror
    // Honua.Core's SupportedFileFormat enum; esrijson/wkb are additional writer/
    // codec-backed formats that are not import enum members.
    // (canonical name, minimum edition or null)
    // -----------------------------------------------------------------------
    private static IEnumerable<CapabilityDescriptor> BuildDataFormatDescriptors()
    {
        string[] formats =
        [
            "geojson",
            "shapefile",
            "geopackage",
            "gpx",
            "kml",
            "gml",
            "wkt",
            "csv",
            "filegdb",
            "flatgeobuf",
            "geoparquet",
            "esrijson",
            "wkb",
        ];

        foreach (var name in formats)
        {
            yield return new CapabilityDescriptor
            {
                Id = DataFormatIdPrefix + name,
                Category = "format",
                Kind = CapabilityKind.DataFormat,
                Maturity = CapabilityMaturity.Implemented,
                StandardName = name,
            };
        }
    }

    private static HonuaEdition? ResolveMinimumEdition(string? entitlementKey)
        => entitlementKey is not null && EditionByEntitlementKey.TryGetValue(entitlementKey, out var edition)
            ? edition
            : null;
}
