// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsRedisOff;

/// <summary>
/// Owner decision 3 (topology) on top of decision 20, host composition: the real composition
/// root booted in <c>Production</c> with NO Redis connection. A Redis-off cell is a serving +
/// configure + style/render topology, so terminal-journey stage 4 (<c>style.apply-preset</c>)
/// must execute there. honua-release run 38249563601 (aws-ecs, redis-off, image 0e03bdd06)
/// recorded <c>code=unavailable; missingDependency=redis</c> for <c>honua_apply_style_preset</c>.
/// A policy-allowed admin style preset now completes with the canonical handle; Studio
/// compositions, publication requests and the governed admin operations keep the typed Redis
/// refusal.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Update)]
public sealed class RedisOffDirectStylePresetHostTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    // Production enforces a complex bootstrap admin password.
    private const string AdminPassword = "Synthetic-RedisOff-Style-5940!";
    private const string ServiceId = "svc-redis-off-style";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 4242;
    private const string StyleId = "style_redis_off_flood";

    private readonly IStyleCatalog _catalog = Substitute.For<IStyleCatalog>();
    private readonly IMetadataV2StyleGraphSync _graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _catalog.GetStyleAsync(StyleId, Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = StyleId,
            Title = "Flood depth",
            MapLibreStyleJson = "{\"version\":8,\"layers\":[]}",
            StyleVersion = 1,
        });
        _catalog.AssociateLayerAsync(StorageLayerId, StyleId, 0, Arg.Any<CancellationToken>()).Returns(true);

        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-redis-off-style", "Flood Dataset", spatial: new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Polygon,
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
                Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 },
            })
            .AddStorageBinding("bind-redis-off-style", "res-redis-off-style", "public.flood", storageLayerId: StorageLayerId)
            .AddService(ServiceId, "Flood")
            .AddPublication("pub-redis-off-style", ServiceId, "res-redis-off-style",
                layerIndex: LayerIndex, storageBindingId: "bind-redis-off-style")
            .BuildProvider();

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
            })
            .ReplaceService(_catalog)
            .ReplaceService(_graphSync)
            .ReplaceService<IMetadataV2GraphProvider>(graph);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/style.apply-preset/submit")]
    public async Task RedisOffProduction_AdminStylePreset_ExecutesDirectlyWithCanonicalHandle()
    {
        UnavailableOperationProposalStore.IsDurable(_fixture.Services.GetService<IOperationProposalStore>())
            .Should().BeFalse("the host under test has no Redis-backed control plane");
        _fixture.Services.GetRequiredService<IOperationInstanceStore>().GetType().Name
            .Should().Be("UnavailableOperationInstanceStore");

        using var admin = ApiKeyClient(AdminPassword);
        var (status, payload) = await SubmitAsync(admin, StylePresetOperation.OperationId,
            $$$"""{"parameters":{"serviceId":"{{{ServiceId}}}","layerId":"{{{LayerIndex}}}","styleId":"{{{StyleId}}}"}}""");

        status.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var handle = document.RootElement.GetProperty("data");
        handle.GetProperty("status").GetString().Should().Be("Completed", payload);
        handle.GetProperty("operationId").GetString().Should().Be(StylePresetOperation.OperationId);
        handle.GetProperty("operationInstanceId").GetString().Should().StartWith("opinst-");
        handle.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        handle.GetProperty("auditId").GetString().Should().NotBeNullOrWhiteSpace();
        handle.GetProperty("policyDecision").GetString().Should().Be("Allow");
        handle.GetProperty("evidenceRefs").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(NonDurableDirectOperationInstanceStore.NonDurableEvidenceRef);
        await _catalog.Received(1).AssociateLayerAsync(StorageLayerId, StyleId, 0, Arg.Any<CancellationToken>());
        await _graphSync.Received(1).SyncLayerStylesAsync(StorageLayerId, Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/studio.draft.create/submit")]
    public async Task RedisOffProduction_AdminGovernedOperations_KeepTypedRedisRefusal()
    {
        using var admin = ApiKeyClient(AdminPassword);

        // Geoprocessing runs on the durable job runtime (journey stage 5), which refuses
        // separately; through the operation catalog, Studio compositions, publication requests
        // and the governed honua_admin_* operations keep the typed Redis refusal.
        foreach (var operationId in new[]
                 {
                     StudioDraftOperations.Create,
                     StudioDraftOperations.CreatePublicationRequest,
                     "admin.layer.style.set",
                 })
        {
            var (status, payload) = await SubmitAsync(admin, operationId, """{"parameters":{}}""");

            status.Should().Be(HttpStatusCode.ServiceUnavailable, $"{operationId}: {payload}");
            using var document = JsonDocument.Parse(payload);
            document.RootElement.GetProperty("missingDependency").GetString()
                .Should().Be(CapabilityUnavailableCodes.RedisDependency, payload);
        }

        await _catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
    }

    private HttpClient ApiKeyClient(string key)
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));

    private static async Task<(HttpStatusCode Status, string Payload)> SubmitAsync(
        HttpClient client,
        string operationId,
        string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operations/{operationId}/submit")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
