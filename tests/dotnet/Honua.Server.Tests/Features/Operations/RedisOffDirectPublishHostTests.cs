// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsRedisOff;

/// <summary>
/// Owner decision 20 (2026-10-10), host composition: the real composition root booted in
/// <c>Production</c> with NO Redis connection. The terminal journey's operator publishes through
/// <c>honua_publish_service</c>; before the ruling stage 3 of honua-release run 38066103745
/// (aws-serverless/redis-off) recorded <c>code=unavailable; missingDependency=redis</c> for an
/// <c>admin:write</c> key, so nothing was ever published on a Redis-off cell. A policy-allowed
/// admin publish now completes with the canonical handle fields; a layer write key is still
/// refused, and a proposal-requiring path keeps the typed Redis refusal.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Create)]
public sealed class RedisOffDirectPublishHostTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    // Production enforces a complex bootstrap admin password.
    private const string AdminPassword = "Synthetic-RedisOff-Publish-5813!";

    private readonly ILayerPublishingService _publishing = Substitute.For<ILayerPublishingService>();
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _publishing
            .ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default",
            });
        _publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 321,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default",
            });
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        _fixture
            .UseEnvironment(Environments.Production)
            .ConfigureWebHost(builder =>
            {
                // Deliberately no ConnectionStrings:redis.
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);

                // Operator rules, so the test does not depend on the Production built-in defaults.
                builder.UseSetting("Operations:Policy:Enabled", "true");
                builder.UseSetting("Operations:Policy:DefaultDecision", "Deny");
                builder.UseSetting("Operations:Policy:Rules:0:Role", "admin");
                builder.UseSetting("Operations:Policy:Rules:0:Decision", "Allow");
                builder.UseSetting("Operations:Policy:Rules:1:Role", "scoped-admin-key");
                builder.UseSetting("Operations:Policy:Rules:1:Decision", "Allow");
            })
            .ReplaceService(_publishing)
            .ReplaceService(resolver);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task RedisOffProduction_AdminKeyPublish_CompletesWithCanonicalHandle()
    {
        _fixture.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.Should().Be(Environments.Production);
        UnavailableOperationProposalStore.IsDurable(_fixture.Services.GetService<IOperationProposalStore>())
            .Should().BeFalse("the host under test has no Redis-backed control plane");
        _fixture.Services.GetRequiredService<IOperationInstanceStore>().GetType().Name
            .Should().Be("UnavailableOperationInstanceStore");

        using var admin = ApiKeyClient(AdminPassword);
        var output = await CallToolAsync(admin, PublishArguments("admin-parcels"));

        AssertCanonicalDirectPublish(output);
    }

    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task RedisOffProduction_AdminWriteKeyPublish_CompletesWithCanonicalHandle()
    {
        var key = await CreateKeyAsync("redis-off-operator", ["admin:write"]);
        using var operatorClient = ApiKeyClient(key);

        var output = await CallToolAsync(operatorClient, PublishArguments("operator-parcels"));

        AssertCanonicalDirectPublish(output);
    }

    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task RedisOffProduction_LayerWriteKeyPublish_IsRefusedWithoutPublishing()
    {
        var key = await CreateKeyAsync("redis-off-layer-writer", ["write:*"]);
        using var writer = ApiKeyClient(key);

        var (status, payload) = await PostToolCallAsync(writer, PublishArguments("writer-parcels"));

        if (status == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var refused = root.TryGetProperty("error", out _)
                || (root.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean())
                || root.GetProperty("result").GetProperty("structuredContent").GetProperty("status").GetString() == "Denied";
            refused.Should().BeTrue($"a layer write key must not publish: {payload}");
        }
        else
        {
            status.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], payload);
        }

        await _publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(),
            Arg.Is<LayerPublishRequest>(request => request.LayerName == "writer-parcels"),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task RedisOffProduction_Manifest_StillReportsProposalsUnavailable()
    {
        using var admin = ApiKeyClient(AdminPassword);
        using var response = await admin.GetAsync("/api/v1/capabilities/manifest");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var proposals = document.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
        proposals.GetProperty("available").GetBoolean().Should().BeFalse(proposals.GetRawText());
    }

    private void AssertCanonicalDirectPublish(JsonElement output)
    {
        output.GetProperty("status").GetString().Should().Be("Completed", output.GetRawText());
        output.GetProperty("operationId").GetString().Should().Be(ServicePublishOperation.OperationId);
        var operationInstanceId = output.GetProperty("operationInstanceId").GetString();
        operationInstanceId.Should().StartWith("opinst-");
        output.GetProperty("handleId").GetString().Should().Be(operationInstanceId);
        output.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        output.GetProperty("auditId").GetString().Should().NotBeNullOrWhiteSpace();
        output.GetProperty("policyOutcome").GetString().Should().Be("Allow");
        output.GetProperty("authorizationOutcome").GetString().Should().NotBeNullOrWhiteSpace();
        output.GetProperty("durable").GetBoolean().Should().BeFalse();
        output.GetProperty("evidenceRefs").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(NonDurableDirectOperationInstanceStore.NonDurableEvidenceRef);
        output.GetProperty("layerId").GetString().Should().Be("321");
    }

    private static string PublishArguments(string layerName)
        => $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","table":"parcels","layerName":"{{layerName}}","serviceName":"default"}""";

    private async Task<string> CreateKeyAsync(string name, IReadOnlyList<string> permissions)
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync(name, permissions, DateTimeOffset.UtcNow.AddMinutes(10), "redis-off-test", CancellationToken.None);
        return created.Key;
    }

    private HttpClient ApiKeyClient(string key)
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string argumentsJson)
    {
        var (status, payload) = await PostToolCallAsync(client, argumentsJson);
        status.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(payload);
        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse(payload);
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<(HttpStatusCode Status, string Payload)> PostToolCallAsync(HttpClient client, string argumentsJson)
    {
        var body = $$"""{"jsonrpc":"2.0","id":"publish","method":"tools/call","params":{"name":"honua_publish_service","arguments":{{argumentsJson}}}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
