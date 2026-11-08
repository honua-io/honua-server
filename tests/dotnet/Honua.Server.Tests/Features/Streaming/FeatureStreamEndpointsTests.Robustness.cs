// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    public static TheoryData<string, string> UnsupportedStreamOwners => new()
    {
        { "services", "{\"serviceType\":\"future-service\"}" },
        { "services", "{\"status\":{\"lifecycle\":\"future-lifecycle\"}}" },
        { "services", "{\"status\":{\"lifecycle\":-1}}" },
        { "publications", "{\"publicationType\":\"future-layer\"}" },
        { "publications", "{\"status\":{\"lifecycle\":\"future-lifecycle\"}}" },
        { "publications", "{\"status\":{\"lifecycle\":-1}}" }
    };

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    [MemberData(nameof(UnsupportedStreamOwners))]
    public async Task Capabilities_UnsupportedOwner_OmitsLayer(string collection, string patch)
    {
        var provider = await UnsupportedStreamProviderAsync(collection, patch);
        await using var fixture = StreamRobustnessFixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync("/api/v1/streaming/features/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("data").GetProperty("layers").GetArrayLength().Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [MemberData(nameof(UnsupportedStreamOwners))]
    public async Task Sse_UnsupportedOwner_RejectsSnapshotWithoutReading(string collection, string patch)
    {
        var provider = await UnsupportedStreamProviderAsync(collection, patch);
        var reader = Substitute.For<IFeatureReader>();
        await using var fixture = StreamRobustnessFixture(provider).ReplaceService<IFeatureReader>(reader);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        reader.ClearReceivedCalls();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var mode in new[] { "snapshot", "snapshot-then-delta" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/streaming/features?layers=0&mode={mode}");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(cancellation.Token)).Should().Contain("Layer 0 not found");
        }
        reader.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [MemberData(nameof(UnsupportedStreamOwners))]
    public async Task WebSocket_UnsupportedOwner_RejectsSnapshotWithoutReading(string collection, string patch)
    {
        var provider = await UnsupportedStreamProviderAsync(collection, patch);
        var reader = Substitute.For<IFeatureReader>();
        await using var fixture = StreamRobustnessFixture(provider).ReplaceService<IFeatureReader>(reader);
        await fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features"), cancellation.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, cancellation.Token);
        reader.ClearReceivedCalls();
        foreach (var mode in new[] { "snapshot", "snapshot-then-delta" })
        {
            await SendWebSocketJsonAsync(socket,
                $$"""{"type":"subscribe","subscriptionId":"rejected","layerId":0,"mode":"{{mode}}"}""", cancellation.Token);
            var error = await ReceiveWebSocketJsonAsync(socket, cancellation.Token);

            error.GetProperty("type").GetString().Should().Be("error");
            error.GetProperty("code").GetString().Should().Be("invalid-subscription");
            error.GetProperty("message").GetString().Should().Contain("Layer 0 not found");
        }
        reader.ReceivedCalls().Should().BeEmpty();
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Complete", cancellation.Token);
    }

    private static WebAppFixture StreamRobustnessFixture(TestMetadataV2GraphProvider provider)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IMetadataV2GraphProvider>(provider);

    private static async Task<TestMetadataV2GraphProvider> UnsupportedStreamProviderAsync(string collection, string patch)
    {
        var provider = BuildStreamMetadataProvider(false);
        var snapshot = await provider.GetCurrentAsync();
        var node = JsonSerializer.SerializeToNode(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        foreach (var property in JsonNode.Parse(patch)!.AsObject())
        {
            node[collection]![0]![property.Key] = property.Value?.DeepClone();
        }
        provider.SetGraph(JsonSerializer.Deserialize(node.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!);
        return provider;
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    [InlineData("{\"type\":\"future-connection\"}", 0)]
    [InlineData("{\"type\":\"managed\",\"status\":{\"lifecycle\":\"future-lifecycle\"}}", 0)]
    [InlineData("{\"type\":\"managed\"}", 1)]
    public async Task Capabilities_ConnectionKindOrLifecycleUnrecognized_OmitsUnsupportedBinding(string json, int expectedLayers)
    {
        var provider = BuildStreamMetadataProvider(false);
        var graph = (await provider.GetCurrentAsync()).Graph;
        var connection = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Connection)! with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "stream-connection" }
        };
        provider.SetGraph(graph with
        {
            Connections = [connection],
            StorageBindings = graph.StorageBindings.Select(binding => binding with { ConnectionId = connection.Metadata.Id }).ToArray()
        });
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IMetadataV2GraphProvider>(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/streaming/features/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("layers").GetArrayLength().Should().Be(expectedLayers);
    }
}
