// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Endpoint-level backing coverage for the admin named-replica inspection routes
/// (#1167). The GeoServices-side suite already exercises these routes, but it builds
/// the request path through a helper, which the EndpointRegistry drift source-scanner
/// (<see cref="Honua.Server.Tests.Features.API.EndpointRegistryDriftTests"/>) cannot
/// associate with the route. These tests issue the same-method HTTP request with the
/// literal path inline so the admin replica registry entries are recognized as backed.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ReplicaInfo)]
public sealed class AdminReplicaManagementDriftBackingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AdminReplicaManagementDriftBackingTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas")]
    public async Task ListReplicas_ForSeededService_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/replicas");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceId}/replicas/{replicaId}")]
    public async Task GetReplica_UnknownReplica_ReturnsNotFound()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/replicas/00000000-0000-0000-0000-000000000000");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
