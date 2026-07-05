// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Capabilities;

/// <summary>
/// Track-B integration coverage for honua-server#2347 (T11): the built-experimental
/// capabilities the T10 flip (#2346) gated OFF the first-release surface
/// (<c>temporal.*</c>, <c>sync.offline</c>, <c>realtime.feature-streams</c>,
/// <c>security.mtls</c>; <c>alerts.geofence</c> was promoted to GA in #2427 and is no
/// longer experimental) must be genuinely <b>absent</b> from
/// every served surface end-to-end when experimental is disabled (the production
/// default), and become present/served the moment a customer opts one in via
/// <c>Capabilities:Experimental</c>. This closes the loop B2 (#2334) and B3 (#2335)
/// opened at the registry/composition layer by asserting the posture at the wire:
/// <list type="bullet">
///   <item><description>
///     the registry-derived capability manifest (<c>GET /api/v1/capabilities/manifest</c>)
///     omits the experimental capability ids while disabled and includes exactly the
///     opted-in one;
///   </description></item>
///   <item><description>
///     the gated HTTP route groups (<c>/api/v1/temporal/*</c>,
///     <c>/api/v1/admin/operations/streaming</c>) short-circuit with
///     <c>404 honua:capability-experimental-disabled</c> while disabled, and open
///     per-capability when opted in.
///   </description></item>
/// </list>
/// The Test environment turns experimental ON globally (appsettings.Test.json), which is
/// why the existing temporal/alerts/streaming suites prove the served (enabled) path; these
/// tests override that switch OFF to prove the absent (default) posture the flip introduced.
/// No experimental capability projects onto the <c>/mcp</c> tool/resource roster (every
/// <c>protocol.mcp.*</c> descriptor is Implemented), so the <c>/mcp</c> catalog carries none
/// of them by construction — asserted by <c>McpRegistryCompositionTests</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Metadata)]
public sealed class ExperimentalCapabilityGatingIntegrationTests
{
    /// <summary>The built-experimental manifest capability ids the T10 flip (#2346) gates off.</summary>
    private static readonly string[] ExperimentalCapabilityIds =
    [
        "temporal.filtering",
        "temporal.extent-discovery",
        "temporal.histogram",
        "temporal.time-series-tiles",
        "sync.offline",
        "realtime.feature-streams",
        // alerts.geofence promoted to GA (Implemented) in #2427 — no longer experimental-gated.
        "security.mtls",
        // versioning.branch (VMS REST surface) gated Preview in the BH6-001/BH6-002 fix batch.
        "versioning.branch",
    ];

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_WhenExperimentalDisabled_OmitsEveryExperimentalCapability()
    {
        // Registry-derived manifest (the production composition) with experimental OFF:
        // every built-experimental capability resolves experimental-disabled, so the
        // manifest omits it entirely (wire-compatible absence), while the in-release
        // capabilities stay present.
        var fixture = CreateFixture(experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            var root = document.RootElement;

            foreach (var experimentalId in ExperimentalCapabilityIds)
            {
                HasCapability(root, experimentalId).Should().BeFalse(
                    "the experimental capability {0} is gated OFF the first-release manifest by default",
                    experimentalId);
            }

            // In-release capabilities are unaffected by the flip — still served.
            HasCapability(root, "query.features").Should().BeTrue();
            HasCapability(root, "edit.features").Should().BeTrue();
            HasCapability(root, "package.metadata-v2").Should().BeTrue();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_WhenExperimentalEnabledPerCapability_IncludesExactlyThatCapability()
    {
        // Opt a single experimental capability in (global switch still OFF). It must
        // reappear in the served manifest while its experimental siblings stay omitted —
        // proving the gate is a real, per-capability lever, not an always-off constant.
        var fixture = CreateFixture(
            experimentalGlobalEnabled: false,
            perCapabilityEnabled: "temporal.filtering");
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            var root = document.RootElement;

            HasCapability(root, "temporal.filtering").Should().BeTrue(
                "the opted-in experimental capability is served");

            foreach (var stillDisabled in ExperimentalCapabilityIds.Where(
                id => !string.Equals(id, "temporal.filtering", StringComparison.Ordinal)))
            {
                HasCapability(root, stillDisabled).Should().BeFalse(
                    "{0} was not opted in and stays absent", stillDisabled);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    public async Task TemporalEndpoint_WhenExperimentalDisabled_Returns404ExperimentalDisabled()
    {
        // The gated route group short-circuits BEFORE the handler, so this holds without
        // any temporal graph/layer setup: an authenticated admin reaches the gate and the
        // disabled-experimental capability is indistinguishable from an unmapped route.
        var fixture = CreateFixture(experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(
                "/api/v1/temporal/services/svc-experimental/layers/1/capabilities");

            await AssertExperimentalDisabledAsync(response);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    public async Task VmsEndpoint_WhenExperimentalDisabled_Returns404ExperimentalDisabled()
    {
        // The VMS REST surface (versioning.branch) is gated Preview in the BH6-001/BH6-002 fix
        // batch. With experimental OFF the capability-gate filter must short-circuit with
        // 404 honua:capability-experimental-disabled before any handler runs — proving the GA
        // surface no longer exposes the VMS endpoints by default.
        var fixture = CreateFixture(experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(
                "/rest/services/svc-vms-gate-test/VersionManagementServer?f=json");

            await AssertExperimentalDisabledAsync(response);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    public async Task AlertsEndpoint_AfterGaPromotion_IsNotExperimentalGated()
    {
        // #2427 promoted alerts.geofence Experimental -> Implemented (GA). The alerts
        // admin group must no longer short-circuit as experimental-disabled even with the
        // global experimental switch OFF — the request reaches the handler (subject to
        // admin auth) instead of the 404 the T10 flip previously produced.
        var fixture = CreateFixture(experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/admin/alerts/zones");

            await AssertNotExperimentalDisabledAsync(response);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    public async Task GatedEndpoints_WhenExperimentalEnabledPerCapability_OpenOnlyThatCapability()
    {
        // Opt temporal.filtering in but leave realtime.feature-streams off. The temporal
        // group's gate must OPEN (no longer the experimental-disabled 404 — the request
        // reaches the handler), while the still-disabled streaming group keeps returning
        // the experimental-disabled 404. One fixture proves the per-capability gate both
        // ways. (Alerts is no longer a valid still-disabled control after its #2427 GA
        // promotion, so this uses the streaming operations group instead.)
        var fixture = CreateFixture(
            experimentalGlobalEnabled: false,
            perCapabilityEnabled: "temporal.filtering");
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();

            using var temporalResponse = await client.GetAsync(
                "/api/v1/temporal/services/svc-experimental/layers/1/capabilities");
            await AssertNotExperimentalDisabledAsync(temporalResponse);

            using var streamingResponse = await client.GetAsync(
                "/api/v1/admin/operations/streaming/subscribers");
            await AssertExperimentalDisabledAsync(streamingResponse);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(
        bool experimentalGlobalEnabled,
        string? perCapabilityEnabled = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["MultiTenancy:DefaultTenantId"] = "tenant-experimental",
            // Production composition: the manifest omits experimental-disabled capabilities.
            ["Capabilities:ManifestFromRegistry"] = "true",
            // Override the Test-environment default (Experimental:Enabled=true) so the
            // disabled posture — the production default — is exercised end-to-end.
            ["Capabilities:Experimental:Enabled"] = experimentalGlobalEnabled ? "true" : "false",
        };

        if (perCapabilityEnabled is not null)
        {
            configuration[$"Capabilities:Experimental:{perCapabilityEnabled}:Enabled"] = "true";
        }

        return new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                    configurationBuilder.AddInMemoryCollection(configuration));
            });
    }

    private static async Task AssertExperimentalDisabledAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a disabled-experimental capability is indistinguishable from an unmapped route");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var document = await ReadDocumentAsync(response);
        document.RootElement.GetProperty("type").GetString()
            .Should().Be(CapabilityGateEndpointFilter.ExperimentalDisabledProblemType);
    }

    private static async Task AssertNotExperimentalDisabledAsync(HttpResponseMessage response)
    {
        // The gate is the ONLY producer of the experimental-disabled problem type, so its
        // absence proves the gate opened — regardless of whatever the handler then returns
        // for the (deliberately unseeded) service/layer.
        var payload = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.NotFound
            && response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("type", out var type))
            {
                type.GetString().Should().NotBe(
                    CapabilityGateEndpointFilter.ExperimentalDisabledProblemType,
                    "the opted-in capability's gate must have opened");
            }
        }
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static bool HasCapability(JsonElement root, string capabilityId)
        => root.GetProperty("capabilities").EnumerateArray().Any(item =>
            string.Equals(item.GetProperty("id").GetString(), capabilityId, StringComparison.Ordinal));
}
