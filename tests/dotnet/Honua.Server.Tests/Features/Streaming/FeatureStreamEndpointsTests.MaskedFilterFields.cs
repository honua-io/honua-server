// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

// A stream filter follows the feature-query rule: a field hidden from the caller cannot be used
// by a predicate, on either transport.
public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_FilterOverMaskedField_ReturnsBadRequest()
    {
        var fixture = CreatePolicyRefreshFixture(MaskField("description"));
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await OpenFilteredSseAsync(client, "filter=" + Uri.EscapeDataString("description = 'x'"));
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("Field 'description' is masked");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_FilterOverReadableField_WithAnotherFieldMasked_OpensStream()
    {
        var fixture = CreatePolicyRefreshFixture(MaskField("description"));
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await OpenFilteredSseAsync(client, "filter=" + Uri.EscapeDataString("name = 'x'"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_TemporalFilterOverMaskedField_ReturnsBadRequest()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IMetadataV2GraphProvider>(BuildStreamMetadataProvider(timeAware: true))
            .ReplaceService(MaskField("timestamp"));
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await OpenFilteredSseAsync(client, "datetime=2023-01-01T00:00:00Z/2023-02-01T00:00:00Z");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("Field 'timestamp' is masked");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_SubscribeFilterOverMaskedField_ReturnsErrorAndKeepsConnection()
    {
        var fixture = CreatePolicyRefreshFixture(MaskField("description"));
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?clientLabel=masked-filter"), cts.Token);
            _ = await ReceiveWebSocketJsonAsync(socket, cts.Token);

            await SendWebSocketJsonAsync(
                socket, """{"type":"subscribe","subscriptionId":"hidden","layerId":0,"filter":"description = 'x'"}""", cts.Token);
            var refused = await ReceiveWebSocketJsonAsync(socket, cts.Token);
            refused.GetProperty("type").GetString().Should().Be("error");
            refused.GetProperty("code").GetString().Should().Be("invalid-subscription");
            refused.GetProperty("message").GetString().Should().Contain("Field 'description' is masked");

            await SendWebSocketJsonAsync(
                socket, """{"type":"subscribe","subscriptionId":"readable","layerId":0,"filter":"name = 'x'"}""", cts.Token);
            (await ReceiveWebSocketJsonAsync(socket, cts.Token)).GetProperty("status").GetString().Should().Be("subscribed");

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> OpenFilteredSseAsync(HttpClient client, string valueFilter)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/streaming/features?layers=0&{valueFilter}");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    private static IFieldMaskPolicyStore MaskField(string attribute)
    {
        var masks = Substitute.For<IFieldMaskPolicyStore>();
        masks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FieldMaskPolicy[] { new() { Role = "*", Service = "*", Layer = "*", Attribute = attribute } });
        return masks;
    }
}
