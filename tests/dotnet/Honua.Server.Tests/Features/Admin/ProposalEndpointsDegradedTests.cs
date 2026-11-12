// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
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

    // The handlers treat an unregistered proposal store and the fail-closed placeholder alike, so
    // the wire tests cannot tell a host that composed no durable store from one whose store
    // failed. Asserting the resolved store directly fails as soon as a durable store is registered
    // for a host without a system database.
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    public void Composition_WithoutRedis_RegistersNeitherDurableProposalStoreNorGateway()
    {
        _fixture.Services.GetService<IOperationProposalStore>().Should().BeOfType<UnavailableOperationProposalStore>(
            "the only durable implementation is Redis-backed and hard-depends on IConnectionMultiplexer, so "
            + "a Redis-less host composes only the fail-closed placeholder");
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
        root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.SystemDatabaseDependency);
        root.GetProperty("capability").GetString().Should().Be(
            CapabilityUnavailableCodes.ControlPlaneProposalsCapability,
            "the refusal joins the operations.proposals manifest entry that reports the same surface unavailable");
        root.GetProperty("remediation").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.SystemDatabaseRemediationRef);
    }
}
