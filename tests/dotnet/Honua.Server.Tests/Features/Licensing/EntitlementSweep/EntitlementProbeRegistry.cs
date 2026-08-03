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

            // Geocoding — the Enterprise endpoint filter runs before request parsing or
            // provider resolution. GeoServices errors are HTTP 200 with error.code=402,
            // which the shared IsBlocked helper recognizes.
            new(FeatureCatalog.BatchGeocodingKey, HttpMethod.Get,
                "/rest/services/World/GeocodeServer/geocodeAddresses"),

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

            // Identity: OIDC (#2997) — the provider admin group gates identity.oidc (Pro);
            // creating a provider when the store already holds one gates the Enterprise
            // identity.oidc-multi-provider first (the sweep fixture pre-seeds one provider so
            // that branch is reachable in a single request).
            new(FeatureCatalog.OidcAuthenticationKey, HttpMethod.Get, "/api/v1/admin/oidc/providers"),
            new(FeatureCatalog.OidcMultiProviderKey, HttpMethod.Post, "/api/v1/admin/oidc/providers",
                """{"name":"sweep-second-provider","providerType":"Generic","authority":"https://idp2.example.com","clientId":"sweep-client-2"}"""),

            // Alerts/Channels (#2998) — rule mutations 402 with one `entitlement: {key}` detail
            // per missing alerts.*/channels.* key BEFORE zone/config validation, so a
            // nonexistent zone or unconfigured channel never masks the gate. At an entitled
            // edition the same request proceeds into validation and 400s (zone not found /
            // channel unconfigured), which the sweep treats as "not blocked" by design.
            new(FeatureCatalog.AlertsEnterExitKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("webhook")),
            new(FeatureCatalog.ChannelsWebhookKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("webhook")),
            new(FeatureCatalog.AlertsDwellKey, HttpMethod.Post, AlertRulesPath,
                """{"serviceId":"entitlement-sweep","layerId":1,"zoneId":1,"ruleName":"sweep dwell probe","triggerType":"dwell","conditionsJson":"{\"dwellSeconds\":60}","cooldownSeconds":30,"severity":"warning","editionRequired":"pro","channels":["webhook"],"isActive":true}"""),
            new(FeatureCatalog.AlertsThresholdKey, HttpMethod.Post, AlertRulesPath,
                """{"serviceId":"entitlement-sweep","layerId":1,"ruleName":"sweep threshold probe","triggerType":"threshold","conditionsJson":"{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}","cooldownSeconds":30,"severity":"warning","editionRequired":"pro","channels":["webhook"],"isActive":true}"""),
            new(FeatureCatalog.AlertsEvaluationKey, HttpMethod.Post, AlertRulesPath + "/test",
                $$"""{"rule":{{EnterRuleJson("webhook")}}}"""),
            new(FeatureCatalog.ChannelsEmailKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("email")),
            new(FeatureCatalog.ChannelsSlackKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("slack")),
            new(FeatureCatalog.ChannelsTeamsKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("microsoft_teams")),
            new(FeatureCatalog.ChannelsAwsSnsKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("aws_sns")),
            new(FeatureCatalog.ChannelsAzureEventGridKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("azure_eventgrid")),
            new(FeatureCatalog.ChannelsDigestKey, HttpMethod.Post, AlertRulesPath,
                EnterRuleJson("digest")),
        }.ToDictionary(p => p.Key, StringComparer.Ordinal);

    private const string AlertRulesPath = "/api/v1/admin/alerts/rules";

    /// <summary>
    /// An enter-trigger rule (Pro tier) carrying the given delivery channel, so each channels.*
    /// probe isolates its own key: at Pro the trigger is entitled and only the probed channel's
    /// key appears in the 402; at Community the combined 402 lists the probed channel key
    /// alongside alerts.enter-exit, which still satisfies the per-key body assertion.
    /// </summary>
    private static string EnterRuleJson(string channel)
        => $$"""{"serviceId":"entitlement-sweep","layerId":1,"zoneId":1,"ruleName":"sweep {{channel}} probe","triggerType":"enter","conditionsJson":"{}","cooldownSeconds":30,"severity":"warning","editionRequired":"pro","channels":["{{channel}}"],"isActive":true}""";

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
            new(FeatureCatalog.GeocodingFailoverKey,
                "Failover is automatic behavior inside forward/reverse/suggest/batch requests, " +
                "not a dedicated HTTP operation. GeocodingHandler and the MCP geocode tool check " +
                "this entitlement and pass allowFailover into the canonical coordinator; an " +
                "unentitled request still uses its primary provider instead of returning 402. " +
                "Verified behaviorally enforced; there is no distinct HTTP 402 surface to probe."),
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
                "and playback integration' per FeatureCatalog doc) — advertised and edition-" +
                "resolved by the shared capability manifest, with no dedicated backend route " +
                "whose response could use the sweep's 402/200 probe shape."),
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
            new(FeatureCatalog.OutputCacheKey,
                "Enforced as a pipeline branch (#2998): Program wires UseOutputCache() inside an " +
                "app.UseWhen(...) branch whose predicate reads the live license snapshot, so the " +
                "gate follows runtime license changes. There is no single HTTP call whose response " +
                "reflects it — an unentitled host serves identical, just uncached, responses. " +
                "Verified enforced in code (Program.cs + OutputCacheEntitlementGateTests); not a gap."),
            new(FeatureCatalog.OidcClaimsMappingKey,
                "Enforced as a config-driven soft-degrade (#2997): the OIDC provider admin DTOs " +
                "carry no claims-mapping fields, so the surface that exists is the " +
                "Oidc:ClaimsMapping configuration (CustomMappings/AdditionalRoleClaimTypes) " +
                "applied by OidcClaimsTransformation at authentication time. When the " +
                "entitlement is missing, custom mappings are skipped (default normalization " +
                "still runs) rather than failing the request, so no 402 is ever produced. " +
                "Verified enforced in code (OidcClaimsMappingEntitlementTests); not a gap."),
        }.ToDictionary(e => e.Key, StringComparer.Ordinal);

    /// <summary>
    /// Known enforcement gaps: the catalog declares a paid edition, an HTTP surface exists,
    /// and no gate protects it today. Loud and minimal by design (#2980) — every entry
    /// must reference the tracking issue that owns its resolution, and deleting an entry (once
    /// fixed) is exactly the signal that resolution landed.
    /// </summary>
    public static IReadOnlyDictionary<string, EntitlementKnownGapEntry> KnownGaps { get; } =
        // #2997/#2998 resolved the last known gaps (OIDC identity.*, Alerts/Channels
        // alerts.*/channels.*, caching.output-cache): every declared Pro/Enterprise key with an
        // HTTP surface is now probed above, or carries a verified no-http-surface entry. Keep
        // this bucket empty — a new entry requires its own tracking issue.
        new Dictionary<string, EntitlementKnownGapEntry>(StringComparer.Ordinal);
}
