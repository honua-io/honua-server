// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// honua-release#202: the durable operation proposal/approval control plane is composed only
/// when Redis is configured, but its routes are mapped unconditionally. On a Redis-less install
/// the surface used to fail with an untyped <c>500</c> that leaked the unresolved DI service
/// name; it must instead refuse with the same machine-readable capability-unavailable receipt
/// the geoprocessing job surfaces emit, so a terminal agent can tell "this install cannot do
/// this" from "this server is broken".
/// </summary>
/// <remarks>
/// The <see cref="WebAppFixture"/> runs without Redis and registers no
/// <c>IOperationProposalStore</c>/<c>IOperationGateway</c>, which is exactly the degraded
/// composition under test. <c>ProposalEndpointsTests</c> covers the composed happy path.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class ProposalEndpointsDegradedTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// honua-server#3599: pins the composition-root gate itself, not just the HTTP projection of it.
    /// </summary>
    /// <remarks>
    /// The wire tests below prove the refusal fires when the durable control plane is absent, but
    /// they cannot distinguish "absent because <c>Program.cs</c> gated the registration" from
    /// "absent because something else failed". The refusal is reached only through the handlers'
    /// optional <c>[FromServices] IOperationProposalStore? = null</c> parameters, which resolve to
    /// null strictly when the service is UNREGISTERED. Registering
    /// <c>RedisOperationProposalStore</c> outside the <c>if (connectedRedis != null)</c> gate would
    /// therefore not make these routes return the typed refusal — it would make DI activation throw
    /// on the unresolvable <c>IConnectionMultiplexer</c> and hand the caller an unhandled
    /// <c>500</c> naming an internal DI type, which is exactly the shape #3599 reported from a
    /// pinned candidate image. Asserting absence here fails the moment that registration moves back
    /// out of the gate, instead of waiting for a release candidate to surface it.
    /// </remarks>
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    public void Composition_WithoutRedis_RegistersNeitherProposalStoreNorGateway()
    {
        _fixture.Services.GetService<IOperationProposalStore>().Should().BeNull(
            "the only implementation is Redis-backed and hard-depends on IConnectionMultiplexer, so "
            + "registering it on a Redis-less host trades a typed 503 for an unhandled 500");
        _fixture.Services.GetService<IOperationGateway>().Should().BeNull(
            "the gateway constructor-injects IOperationProposalStore, so it cannot be composed "
            + "wherever the store is not");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    public async Task ListProposals_WithoutDurableControlPlane_ReturnsTypedCapabilityUnavailableRefusal()
    {
        using var response = await _client.GetAsync("/api/v1/admin/proposals");

        await AssertCapabilityUnavailableAsync(response);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_WithoutDurableControlPlane_ReturnsTypedCapabilityUnavailableRefusal()
    {
        using var response = await _client.GetAsync("/api/v1/admin/proposals/does-not-exist");

        await AssertCapabilityUnavailableAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_WithoutDurableControlPlane_ReturnsTypedCapabilityUnavailableRefusal()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(
            "/api/v1/admin/proposals/does-not-exist/approve",
            content);

        await AssertCapabilityUnavailableAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task RejectProposal_WithoutDurableControlPlane_ReturnsTypedCapabilityUnavailableRefusal()
    {
        using var content = new StringContent("""{"reason":"no"}""", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(
            "/api/v1/admin/proposals/does-not-exist/reject",
            content);

        await AssertCapabilityUnavailableAsync(response);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    public async Task ListProposals_WithRedisConfiguredButUnentitled_ReportsLicenseNotMissingRedis()
    {
        // Redis IS deployed; only the Pro `caching.redis` entitlement is missing, so the control
        // plane was composed out by licensing. "Configure Redis and restart" would be remediation
        // that cannot work, so the receipt names the entitlement instead (honua-release#202).
        var fixture = new WebAppFixture()
            .ConfigureServices(static services =>
                services.Configure<DurableJobSubstrateOptions>(options =>
                {
                    options.RedisConfigured = true;
                    options.RedisEntitled = false;
                }));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/admin/proposals");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementErrorCode);
            root.GetProperty("missingEntitlement").GetString()
                .Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
            root.TryGetProperty("missingDependency", out _).Should().BeFalse(
                "Redis is present; nothing is missing but a licence");
            root.GetProperty("remediation").GetString().Should().NotContain("Set ConnectionStrings__Redis");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// honua-server#3599: the entitlement split is a property of the control plane, not of the read
    /// routes. Approve and reject are the two the terminal journey actually blocks on
    /// (honua-release#123 stages 7/8), and they reach <c>ControlPlaneUnavailable</c> through a
    /// different guard than list does — approve requires BOTH the gateway and the store, reject only
    /// the gateway — so cause classification is asserted on them directly rather than assumed from
    /// the list route.
    /// </summary>
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task ApproveAndRejectProposal_WithRedisConfiguredButUnentitled_ReportsLicenseNotMissingRedis()
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(static services =>
                services.Configure<DurableJobSubstrateOptions>(options =>
                {
                    options.RedisConfigured = true;
                    options.RedisEntitled = false;
                }));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();

            using var approveBody = new StringContent("{}", Encoding.UTF8, "application/json");
            using var approve = await client.PostAsync(
                "/api/v1/admin/proposals/does-not-exist/approve",
                approveBody);
            await AssertLicenseRequiredAsync(approve);

            using var rejectBody = new StringContent(
                """{"reason":"no"}""",
                Encoding.UTF8,
                "application/json");
            using var reject = await client.PostAsync(
                "/api/v1/admin/proposals/does-not-exist/reject",
                rejectBody);
            await AssertLicenseRequiredAsync(reject);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task AssertLicenseRequiredAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("IOperationProposalStore",
            "the refusal must not leak unresolved DI service names to a client");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementErrorCode);
        root.GetProperty("missingEntitlement").GetString()
            .Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
        root.TryGetProperty("missingDependency", out _).Should().BeFalse(
            "Redis is present; nothing is missing but a licence");
        root.GetProperty("remediation").GetString().Should().NotContain("Set ConnectionStrings__Redis");
        root.GetProperty("remediationRef").GetString()
            .Should().Be(CapabilityUnavailableCodes.EntitlementRemediationRef);
        root.TryGetProperty("capability", out _).Should().BeFalse(
            "the manifest has no capability id covering the proposal/approval control plane");
    }

    private static async Task AssertCapabilityUnavailableAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("IOperationProposalStore",
            "the refusal must not leak unresolved DI service names to a client");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
        root.GetProperty("status").GetInt32().Should().Be(503);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
        root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
        root.TryGetProperty("capability", out _).Should().BeFalse(
            "the manifest has no capability id covering the proposal/approval control plane, and "
            + "naming an unrelated one would point a client at a claim that contradicts the refusal");
        root.GetProperty("remediation").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
    }
}
