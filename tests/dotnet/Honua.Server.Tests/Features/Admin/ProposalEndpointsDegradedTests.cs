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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class ProposalEndpointsDegradedTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = CreateUnavailableStoreFixture();
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
    public void Composition_WithoutProposalStore_RegistersNeitherDurableProposalStoreNorGateway()
    {
        _fixture.Services.GetService<IOperationProposalStore>().Should().BeOfType<UnavailableOperationProposalStore>(
            "the fixture explicitly removes persistent proposal storage");
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
    public async Task ListProposals_WithoutStoreAndUnentitledRedis_StillReportsPostgresDependency()
    {
        var fixture = CreateUnavailableStoreFixture()
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

            await AssertCapabilityUnavailableAsync(response);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/reject")]
    public async Task ApproveAndRejectProposal_WithoutStoreAndUnentitledRedis_StillReportPostgresDependency()
    {
        var fixture = CreateUnavailableStoreFixture()
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
            await AssertCapabilityUnavailableAsync(approve);

            using var rejectBody = new StringContent(
                """{"reason":"no"}""",
                Encoding.UTF8,
                "application/json");
            using var reject = await client.PostAsync(
                "/api/v1/admin/proposals/does-not-exist/reject",
                rejectBody);
            await AssertCapabilityUnavailableAsync(reject);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateUnavailableStoreFixture() => new WebAppFixture()
        .ConfigureServices(static services =>
        {
            services.RemoveAll<IOperationProposalStore>();
            services.AddSingleton<IOperationProposalStore, UnavailableOperationProposalStore>();
            services.RemoveAll<IOperationGateway>();
        });

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
        root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.OperationStoreDependency);
        root.GetProperty("capability").GetString().Should().Be(
            CapabilityUnavailableCodes.ControlPlaneProposalsCapability,
            "the refusal joins the operations.proposals manifest entry that reports the same surface unavailable");
        root.GetProperty("remediation").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.OperationStoreRemediationRef);
    }
}
