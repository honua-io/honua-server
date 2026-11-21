// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

// The key type is part of the API key contract: requested at creation, reported on every read.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApiKeyManagement)]
public sealed class AdminApiKeyKindEndpointTests(AdminApiKeyEndpointsTests.ApiKeyHost host)
    : IClassFixture<AdminApiKeyEndpointsTests.ApiKeyHost>
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client = host.Client;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task AgentKind_AddsTheAgentGrantAndIsReportedOnReads()
    {
        var response = await CreateAsync("kind-agent-key", "agent", ["admin:read"]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ApiResponse<AdminApiKeySecretResponse>>(_jsonOptions))!.Data!;
        Assert.Equal("agent", created.ApiKey.Kind);
        Assert.Equal(["admin:read", "agent"], created.ApiKey.Permissions.Order(StringComparer.Ordinal));

        var effective = await _client.GetFromJsonAsync<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>(
            $"/api/v1/admin/api-keys/{created.ApiKey.Id}/effective-permissions", _jsonOptions);
        Assert.Equal("agent", effective!.Data!.Kind);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task StandardKindWithTheAgentGrant_IsRefused()
    {
        var response = await CreateAsync("kind-conflict-key", "standard", ["agent", "admin:read"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot carry", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> CreateAsync(string name, string kind, IReadOnlyList<string> permissions)
        => _client.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest { Name = name, Kind = kind, Permissions = permissions },
            _jsonOptions);
}
