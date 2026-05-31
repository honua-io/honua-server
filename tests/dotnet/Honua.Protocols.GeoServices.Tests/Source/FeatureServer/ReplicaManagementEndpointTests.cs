// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the admin/operator named-replica management API (#1167, slice 1):
/// list + detail of durable named replicas behind <c>RequireAdminAuthorization</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ReplicaManagementEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string ListPath(string serviceId) =>
        $"/api/v1/admin/services/{serviceId}/replicas";

    private static string DetailPath(string serviceId, string replicaId) =>
        $"/api/v1/admin/services/{serviceId}/replicas/{replicaId}";

    private async Task<string> CreateReplicaAsync(string replicaName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName,
            layers = "0",
            syncModel = "perReplica",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("replicaID").GetString()!;
    }

    private static async Task<ApiResponse<ReplicaManagementListResponse>?> ReadListAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementListResponse);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas")]
    public async Task ListReplicas_WhenNoneRegistered_ReturnsEmptyCollection()
    {
        var response = await _fixture.Client.GetAsync(ListPath(WebAppFixture.TestServiceId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadListAsync(response);
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Data.Should().NotBeNull();
        body.Data!.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        body.Data.Replicas.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas")]
    public async Task ListReplicas_AfterCreate_ReturnsRegisteredReplica()
    {
        var replicaId = await CreateReplicaAsync("OperatorReviewReplica");

        var response = await _fixture.Client.GetAsync(ListPath(WebAppFixture.TestServiceId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadListAsync(response);
        body.Should().NotBeNull();
        body!.Data.Should().NotBeNull();

        var match = body.Data!.Replicas.Should().ContainSingle(r => r.ReplicaId == replicaId).Subject;
        match.ReplicaName.Should().Be("OperatorReviewReplica");
        match.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        match.SyncModel.Should().Be("perReplica");
        match.LayerIds.Should().Contain(0);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas")]
    public async Task ListReplicas_ForUnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(ListPath("does-not-exist"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ListReplicas)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas")]
    public async Task ListReplicas_WithoutAdminAuthorization_IsRejected()
    {
        // The admin replica-management group is gated by RequireAdminAuthorization.
        // Disable the dev-auth bypass (which the default shared fixture enables) so the
        // authorization requirement is actually enforced, while staying in the Test
        // environment so configuration validation still passes.
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();

        try
        {
            var anonymousClient = fixture.CreateClient();

            var response = await anonymousClient.GetAsync(ListPath(WebAppFixture.TestServiceId));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // A correctly authenticated admin is not rejected by the authorization gate.
            var adminResponse = await fixture.CreateAdminClient().GetAsync(ListPath(WebAppFixture.TestServiceId));
            adminResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            adminResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_ForRegisteredReplica_ReturnsDetail()
    {
        var replicaId = await CreateReplicaAsync("DetailReplica");

        var response = await _fixture.Client.GetAsync(DetailPath(WebAppFixture.TestServiceId, replicaId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(
            content,
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementDetail);

        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Data.Should().NotBeNull();
        body.Data!.ReplicaId.Should().Be(replicaId);
        body.Data.ReplicaName.Should().Be("DetailReplica");
        body.Data.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        body.Data.SyncModel.Should().Be("perReplica");
        body.Data.LayerIds.Should().Contain(0);
        body.Data.LastSyncGeneration.Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_ForUnknownReplica_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            DetailPath(WebAppFixture.TestServiceId, "00000000000000000000000000000000"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ReplicaInfo)]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_WithoutAdminAuthorization_IsRejected()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();

        try
        {
            var anonymousClient = fixture.CreateClient();

            var response = await anonymousClient.GetAsync(
                DetailPath(WebAppFixture.TestServiceId, "00000000000000000000000000000000"));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var adminResponse = await fixture.CreateAdminClient().GetAsync(
                DetailPath(WebAppFixture.TestServiceId, "00000000000000000000000000000000"));
            adminResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            adminResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
