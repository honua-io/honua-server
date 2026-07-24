// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Tests.Features.Licensing.EntitlementSweep;

/// <summary>
/// A single representative HTTP request that exercises the <c>LicenseGate</c> check for
/// one <see cref="FeatureCatalog"/> entitlement key. The sweep (<see cref="EntitlementSweepEditionTestsBase"/>)
/// sends this request against Community/Pro/Enterprise-licensed hosts and asserts 402 below
/// the key's <see cref="FeatureDefinition.MinimumEdition"/> and non-402 at/above it.
/// </summary>
internal sealed record EntitlementProbe(
    string Key,
    HttpMethod Method,
    string Path,
    string? JsonBody = null,
    Action<HttpRequestMessage>? Configure = null)
{
    public HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(Method, Path);
        if (JsonBody is not null)
        {
            request.Content = new StringContent(JsonBody, Encoding.UTF8, "application/json");
        }

        Configure?.Invoke(request);
        return request;
    }
}

/// <summary>
/// An entitlement key with no request-triggerable HTTP surface to probe (worker/infra
/// entitlement, boot-time gate, dormant scaffolding, or a soft-degrade feature that never
/// returns 402). Mirrors the capability no-surface allowlist pattern
/// (<c>CapabilityKeyCatalog</c>'s Community-only additive list) for the entitlement side.
/// Every entry here must be independently verified (by reading the enforcing code, or its
/// absence) rather than assumed — the reason string is the evidence trail.
/// </summary>
internal sealed record EntitlementNoSurfaceEntry(string Key, string Reason);

/// <summary>
/// A <em>known enforcement gap</em>: the catalog declares a paid edition for this key, an
/// HTTP surface exists for the underlying capability, but no <c>LicenseGate</c> check (or an
/// equivalent, license-derived gate) actually protects it today. This bucket exists so the
/// sweep can land green while the gap is fixed on its own timeline — see
/// <c>docs/internal/contributor/entitlement-sweep-known-gaps.md</c> equivalent context in the
/// PR body. Every entry MUST reference a tracking issue; do not add an entry without one.
/// </summary>
internal sealed record EntitlementKnownGapEntry(string Key, string Reason, string TrackingIssue);

/// <summary>
/// Catalog-generated entitlement probe/allowlist/known-gap registry for the local entitlement
/// sweep (#2980). <see cref="EntitlementSweepCoverageTests"/> asserts every Pro/Enterprise
/// <see cref="FeatureCatalog"/> key resolves to exactly one of <see cref="Probes"/>,
/// <see cref="NoHttpSurfaceAllowlist"/>, or <see cref="KnownGaps"/> — a new gated key with an
/// HTTP surface and no row here fails the suite (fail-closed coverage).
/// </summary>
internal static class EntitlementProbeRegistry
{
    /// <summary>
    /// Every Pro/Enterprise entitlement key in the catalog. Community-tier
    /// <see cref="FeatureCatalog"/> entries (e.g. <c>identity.portal-token</c>,
    /// <c>styling.defaults</c>, <c>temporal.filtering</c>, <c>import.file</c>,
    /// <c>ai.mcp-discovery</c>) are never 402-gated by definition and are excluded from the
    /// sweep's target set.
    /// </summary>
    public static IReadOnlyList<FeatureDefinition> GatedKeys { get; } =
        FeatureCatalog.All.Where(f => f.MinimumEdition > HonuaEdition.Community).ToArray();

    // exportOptions.outputSize is required whenever Layout_Template is MAP_ONLY (mirrors
    // PrintingToolsEndpointTests' DefaultOutputSize) — without it the request 400s before
    // reaching the printing.pdf-output gate.
    private const string WebMapJson =
        """{"mapOptions":{"extent":{"xmin":-122.5,"ymin":37.5,"xmax":-122.0,"ymax":38.0,"spatialReference":{"wkid":4326}}},"operationalLayers":[],"exportOptions":{"dpi":96,"outputSize":[800,600]}}""";

    private const string SpecApplyDocument =
        """{"grammarVersion":"grammar/1.0","processFamilyVersion":"family/1.0","nodes":[{"id":"a","kind":"Compute","op":"compute.noop","inputs":{}}]}""";

    private static void AsEventStream(HttpRequestMessage request)
        => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    private static void WithScimBearer(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IdentityScimBearerToken);

    /// <summary>Shared SCIM bearer token the sweep's fixtures configure via <c>Scim:BearerToken</c>.</summary>
    public const string IdentityScimBearerToken = "entitlement-sweep-scim-token";

    /// <summary>
    /// One representative HTTP request per Pro/Enterprise entitlement key with a real,
    /// probeable HTTP surface. Gate-ordering was verified by reading each handler: every
    /// probe here reaches its <c>LicenseGate</c> check regardless of whether the referenced
    /// resource (service/layer/dataset id) exists, EXCEPT where a comment says otherwise, in
    /// which case the probe uses the seeded <c>test</c> service / layer 0 (temporal-enabled)
    /// so resource validation upstream of the gate succeeds.
    /// </summary>
    public static IReadOnlyDictionary<string, EntitlementProbe> Probes { get; } =
        new EntitlementProbe[]
        {
            // Identity — mTLS/SAML/SCIM already have dedicated gate tests
            // (IdentityEntitlementGateTests, ClientCertificateAdminEndpointsTests); mirrored
            // here as the catalog-driven cross-check.
            new(FeatureCatalog.MtlsClientCertificateKey, HttpMethod.Get,
                "/api/v1/admin/security/client-certificates/profiles"),
            new(FeatureCatalog.SamlAuthenticationKey, HttpMethod.Get, "/saml/metadata"),
            new(FeatureCatalog.ScimProvisioningKey, HttpMethod.Get, "/scim/v2/Users",
                Configure: WithScimBearer),

            // Import — gate runs first in both handlers (raw HttpContext, no FromBody).
            new("import.geoservices", HttpMethod.Post, "/api/v1/admin/import/geoservices/discover", "{}"),
            new("import.geoserver", HttpMethod.Post, "/api/v1/admin/import/geoserver/discover", "{}"),

            // Streaming — RequireProEdition fires before any subscription parsing, but the
            // handler itself 400s first without an SSE Accept header or WebSocket upgrade.
            new("streaming.feature-subscriptions", HttpMethod.Get, "/api/v1/streaming/features",
                Configure: AsEventStream),

            // Field ops — HandleGetCompatibility gates before resolving formId.
            new(FeatureCatalog.FieldOpsOfflineSyncKey, HttpMethod.Get,
                "/api/v1/forms/packages/does-not-matter/compatibility"),

            // Static map — dimensions/dpi/overlays chosen to land strictly between the
            // Community and Pro caps (StaticMapModels.cs) so each gate fires independently.
            new("staticmap.large-dimensions", HttpMethod.Get,
                "/static/test/-122.4194,37.7749,12/2000x2000.png"),
            new("staticmap.high-dpi", HttpMethod.Get,
                "/static/test/-122.4194,37.7749,12/400x300.png?dpi=150"),
            new("staticmap.rich-overlays", HttpMethod.Get,
                "/static/test/-122.4194,37.7749,12/400x300.png?markers=" + BuildMarkers(15)),

            // Raster — multidim job-status gate runs before job-id resolution.
            new("raster.multidim-coverage", HttpMethod.Get,
                "/api/v1/admin/multidim-coverages/jobs/does-not-matter"),

            // Analytics — all four handlers gate before reading the POST body.
            new("analytics.clustering", HttpMethod.Post,
                "/rest/services/test/FeatureServer/0/queryClusters", "{}"),
            new("analytics.spatial-join", HttpMethod.Post,
                "/rest/services/test/FeatureServer/0/spatialJoin", "{}"),
            new("analytics.buffer-aggregate", HttpMethod.Post,
                "/rest/services/test/FeatureServer/0/queryBufferAggregate", "{}"),
            new("analytics.density", HttpMethod.Post,
                "/rest/services/test/FeatureServer/0/queryDensity", "{}"),

            // Analytics — elevation surfaces gate before reading the POST body; dataset "0"
            // is the default seeded elevation surface used by VisibilityAnalysisEndpointTests
            // / SceneAnalysisEndpointTests (depth pack 1, #2960).
            new("analytics.line-of-sight", HttpMethod.Post, "/elevation/0/line-of-sight", "{}"),
            new("analytics.viewshed", HttpMethod.Post, "/elevation/0/viewshed", "{}"),
            new("analytics.sun-shadow", HttpMethod.Post, "/elevation/0/sun-shadow", "{}"),
            new("analytics.slice", HttpMethod.Post, "/elevation/0/slice", "{}"),

            // Temporal — queryDateBins validates the (seeded, valid) service/layer before
            // gating; the tiles gate only fires once a real temporal filter parses, so the
            // probe uses layer 0 (timeInfo seeded) with an instant `time` value.
            new("temporal.histogram", HttpMethod.Get,
                "/rest/services/test/FeatureServer/0/queryDateBins"),
            new("temporal.time-series-tiles", HttpMethod.Get,
                "/tiles/0/0/0/0.mvt?time=2020-01-01T00:00:00Z"),

            // Printing — printing.layout-templates fires for any non-MAP_ONLY template;
            // printing.pdf-output fires for Format=PDF regardless of template. Both checked
            // via GET with the WebMap JSON URL-encoded, mirroring PrintingToolsEndpointTests.
            new("printing.layout-templates", HttpMethod.Get,
                "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute"
                    + $"?Web_Map_as_JSON={Uri.EscapeDataString(WebMapJson)}&Format=PNG32&Layout_Template=Letter%20ANSI%20A%20Portrait"),
            new("printing.pdf-output", HttpMethod.Get,
                "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute"
                    + $"?Web_Map_as_JSON={Uri.EscapeDataString(WebMapJson)}&Format=PDF&Layout_Template=MAP_ONLY"),

            // Editing — applyEdits gates first in the shared edits handler; branch versioning
            // sits behind the versioning.branch experimental capability gate (enabled by the
            // sweep fixtures) and then its own entitlement check.
            new(FeatureCatalog.FeatureServerEditsKey, HttpMethod.Post,
                "/rest/services/test/FeatureServer/0/applyEdits", "{}"),
            new(FeatureCatalog.BranchVersioningKey, HttpMethod.Get,
                "/rest/services/test/VersionManagementServer/versions"),

            // AI — ai.spec-apply needs a plannable no-op document (ComputeNode "compute.noop",
            // mirrors SpecEndpointsTests) so the request reaches the final entitlement gate
            // instead of failing plan validation first; ai.grounding needs a non-empty `turn`
            // AND a `spec` field (an empty object is accepted as "start a new spec" —
            // TryReadSpecDocument 400s on a missing/null spec before the gate runs);
            // ai.workflow-generation needs a non-empty `prompt`. All three read the body
            // manually and validate-then-gate, so a body that fails validation short-circuits
            // before the 402 check.
            new(FeatureCatalog.AiSpecApplyKey, HttpMethod.Post, "/v1/spec/apply",
                SpecApplyDocument, AsEventStream),
            new(FeatureCatalog.AiGroundingKey, HttpMethod.Post, "/v1/grounding/spec/mutate",
                """{"turn":"add a point layer","spec":{}}"""),
            new(FeatureCatalog.AiWorkflowGenerationKey, HttpMethod.Post,
                "/api/v1/analysis/content/generate", """{"prompt":"summarize this layer"}"""),

            // Scene ingest — Enterprise gate runs before any multipart-form parsing.
            new(FeatureCatalog.SceneBimIngestKey, HttpMethod.Post,
                "/api/v1/admin/scenes/ingest/citygml"),
            new(FeatureCatalog.ScenePointCloudIngestKey, HttpMethod.Post,
                "/api/v1/admin/scenes/ingest/pointcloud"),
        }.ToDictionary(p => p.Key, StringComparer.Ordinal);

    private static string BuildMarkers(int count)
        => string.Join('|', Enumerable.Range(0, count).Select(i => $"-122.{400 + i},37.7{i},red"));

    /// <summary>
    /// Entitlement keys with no request-triggerable HTTP 402 surface. Each reason is the
    /// evidence read directly from the enforcing (or non-existent) code path — see the sweep
    /// PR body for the full audit trail.
    /// </summary>
    public static IReadOnlyDictionary<string, EntitlementNoSurfaceEntry> NoHttpSurfaceAllowlist { get; } =
        new EntitlementNoSurfaceEntry[]
        {
            new("raster.cloud-storage-config",
                "Cloud storage connections are configured exclusively via CloudStorageOptions " +
                "(appsettings/environment), with no runtime admin endpoint that mutates them — " +
                "nothing to send an HTTP request to."),
            new("raster.cloud-cog-serving",
                "Verified enforced in code (CogTileResolver.GetTileForLayerAsync checks " +
                "'raster.cloud-cog-serving' — but only once >=1 COG is registered for the " +
                "layer; ImageServerTileHandler falls through to this path only on a local-tile " +
                "cache miss). Probing requires seeding a COG-backed ImageServer layer, which is " +
                "out of scope for this sweep's minimal-fixture design. NOT a known gap — tracked " +
                "as a follow-up to add COG fixture seeding, not a decision ticket."),
            new("raster.temporal-mosaic",
                "Verified enforced in code (ImageServerMosaicHelpers.RequireTemporalMosaicAccess, " +
                "called from 8 ImageServer handler sites) but only reachable through a " +
                "registered, time-aware ImageServer mosaic layer absent from the default " +
                "integration seed. NOT a known gap — a fixture-scope limitation, mirrors the " +
                "raster.cloud-cog-serving entry above."),
            new("caching.redis",
                "Gated at process boot (StartupConfigurationHelpers.IsRedisCacheEntitledAsync " +
                "decides whether the Redis-backed distributed cache is wired at all before the " +
                "host starts accepting requests), not per-request — there is no single HTTP " +
                "call whose response reflects this gate. Verified enforced in code; not a gap."),
            new("routing.solve",
                "MCP-tool-only surface (RouteTool.cs EntitlementKey); denials surface as a " +
                "JSON-RPC failed_precondition tool error, not an HTTP 402 problem response. " +
                "Verified enforced in code; outside this sweep's REST 402/200 probe shape."),
            new("ai.agent-operations",
                "Enforced via IAgentGuardrailPolicy (EditionAgentGuardrailPolicy), whose " +
                "guardrail level is derived directly from the active license edition — " +
                "Community=Direct(no gate check reached)/Pro=Validated/Enterprise=Approval. " +
                "The gate cannot drift from the license state by construction, and the " +
                "observable 402 at insufficient edition is already produced by the ai.spec-apply " +
                "probe one step later in the same request. Verified enforced in code; not a gap."),
            new("ai.approval-workflows",
                "Same edition-derived guardrail ladder as ai.agent-operations above " +
                "(IAgentGuardrailPolicy); cannot drift from the license by construction."),
            new("temporal.animation-api",
                "Pure capability-advertisement flag ('Capability flag for SDK/admin TimeSlider " +
                "and playback integration' per FeatureCatalog doc) — no backend behavior, no " +
                "endpoint, nothing to gate."),
            new("styling.auto-suggest",
                "Degrades gracefully rather than blocking: AdminStyleSuggestionEndpoints always " +
                "returns 200 (enhanced geometry-only defaults at Community, full analysis at " +
                "Pro) — by design there is no 402 response to probe."),
            new("plugin.sdk",
                "PluginCustomEndpoints only maps routes when a plugin providing ICustomEndpoint " +
                "is registered (none in the integration test host, so /plugins/* is 404 " +
                "regardless of edition), and the gate itself returns 404 (not 402) by design " +
                "when unentitled — a deliberate 'route doesn't exist' posture, not the standard " +
                "402 problem-detail shape this sweep checks."),
            new("dr.backup-automation",
                "Dormant scaffolding (ADR-0048-style): Honua.Core/Features/DisasterRecovery has " +
                "domain records only (BackupSchedule, RecoveryObjectives, BackupRecord, " +
                "RecoveryReadiness) with zero consumers outside FeatureCatalog.cs — no admin " +
                "endpoint, no worker, nothing implemented yet to gate."),
            new("dr.failover", "Same dormant-scaffolding state as dr.backup-automation above."),
            new("dr.cache-backup", "Same dormant-scaffolding state as dr.backup-automation above."),
            new("dr.rto-rpo-reporting", "Same dormant-scaffolding state as dr.backup-automation above."),
        }.ToDictionary(e => e.Key, StringComparer.Ordinal);

    /// <summary>
    /// Known enforcement gaps: the catalog declares a paid edition, an HTTP surface exists,
    /// and no gate protects it today. Loud and minimal by design (#2980/#2981) — every entry
    /// must reference the tracking issue that owns its resolution, and deleting an entry (once
    /// fixed) is exactly the signal that resolution landed.
    /// </summary>
    public static IReadOnlyDictionary<string, EntitlementKnownGapEntry> KnownGaps { get; } =
        new EntitlementKnownGapEntry[]
        {
            // --- Geocoding: documented, decision-pending gap (#2981) ---
            new("geocoding.forward",
                "GeoServices HTTP geocoding endpoints (findAddressCandidates/suggest) carry no " +
                "LicenseGate check; only the MCP geocode tool enforces this entitlement today.",
                "#2981"),
            new("geocoding.reverse",
                "GeoServices reverseGeocode carries no LicenseGate check; only the MCP geocode " +
                "tool enforces this entitlement today.",
                "#2981"),
            new("geocoding.failover",
                "No dedicated HTTP surface (failover is automatic behavior inside forward/reverse " +
                "calls); those calls are unenforced per the geocoding.forward/reverse entries " +
                "above, so there is no gated code path to probe for this key either.",
                "#2981"),
            new("geocoding.batch",
                "GeoServices geocodeAddresses carries no LicenseGate check (declared Enterprise; " +
                "not even Pro-gated in practice).",
                "#2981"),

            // --- Identity: OIDC family unenforced everywhere (new finding, this sweep) ---
            new("identity.oidc",
                "Zero LicenseGate/entitlement check anywhere in the OIDC surface: " +
                "OidcProviderEndpoints (admin CRUD for provider configuration) and the JWT " +
                "bearer/OIDC authentication pipeline (OidcAuthenticationExtensions) never call " +
                "LicenseGate. A Community deployment can configure and use single-provider OIDC " +
                "authentication for free. Needs its own decision ticket (see PR body).",
                "#2997"),
            new("identity.oidc-multi-provider",
                "Same root cause as identity.oidc: OidcProviderEndpoints has no provider-count " +
                "or edition check, so configuring N providers is unrestricted at any edition.",
                "#2997"),
            new("identity.claims-mapping",
                "Same root cause as identity.oidc: no claims-mapping-specific gate exists; " +
                "provider configuration (including any claims-mapping fields) is unrestricted.",
                "#2997"),

            // --- Alerts/Channels: parallel, license-disconnected edition system (new finding) ---
            new("alerts.enter-exit",
                "Alerts/channels edition enforcement flows entirely through a standalone " +
                "`Alerts:Edition` config option (IAlertEditionPolicy), completely disconnected " +
                "from ILicenseEntitlementService/Licensing:DevGrantEdition — no LicenseGate call " +
                "exists anywhere in the Alerts feature. `Alerts:Edition` defaults to Pro, so a " +
                "Community-licensed deployment gets Enter/Exit triggers and webhook delivery for " +
                "free unless an operator manually sets Alerts:Edition=Community.",
                "#2998"),
            new("alerts.evaluation", "Same Alerts:Edition root cause as alerts.enter-exit above.",
                "#2998"),
            new("channels.webhook", "Same Alerts:Edition root cause as alerts.enter-exit above.",
                "#2998"),
            new("alerts.dwell",
                "Same Alerts:Edition root cause; conversely, this Enterprise trigger requires a " +
                "manual Alerts:Edition=Enterprise config flip even for a genuinely Enterprise- " +
                "licensed deployment (the config default is Pro) — the two knobs can disagree " +
                "in either direction.",
                "#2998"),
            new("alerts.threshold", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.email", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.slack", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.teams", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.aws-sns", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.azure-eventgrid", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),
            new("channels.digest", "Same Alerts:Edition root cause as alerts.dwell above.",
                "#2998"),

            // --- Caching: output-cache unenforced everywhere (new finding) ---
            new("caching.output-cache",
                "Zero LicenseGate/entitlement check anywhere: ASP.NET Core output caching is " +
                "registered unconditionally in Program.cs for every edition. Unlike caching.redis " +
                "(gated at boot, see the no-http-surface allowlist), nothing turns output caching " +
                "off for Community.",
                "#2998"),
        }.ToDictionary(e => e.Key, StringComparer.Ordinal);
}
