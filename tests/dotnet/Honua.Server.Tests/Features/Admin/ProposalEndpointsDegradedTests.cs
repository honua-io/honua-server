// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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
        using var response = await _client.PostAsync(
            "/api/v1/admin/proposals/does-not-exist/approve",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertCapabilityUnavailableAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task RejectProposal_WithoutDurableControlPlane_ReturnsTypedCapabilityUnavailableRefusal()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/proposals/does-not-exist/reject",
            new StringContent("""{"reason":"no"}""", Encoding.UTF8, "application/json"));

        await AssertCapabilityUnavailableAsync(response);
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
