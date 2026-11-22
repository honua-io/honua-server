// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

// A row policy area cannot be evaluated on stream events, so a subscription that names the
// layer is refused instead of opening a stream that never delivers.
public sealed partial class FeatureStreamEndpointsTests
{
    private const string PolicyArea = """{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}""";

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_AreaRowPolicy_NamedLayer_IsRefusedWithForbidden()
    {
        var fixture = CreateAreaPolicyFixture(PolicyArea);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await OpenAreaPolicySseAsync(client, "layers=0");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("Layer 0").And.Contain("feature query");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(PolicyArea, "clientLabel=unscoped")]
    [InlineData(null, "layers=0")]
    public async Task Sse_AreaRowPolicy_UnscopedOrWithoutArea_OpensStream(string? area, string query)
    {
        var fixture = CreateAreaPolicyFixture(area);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await OpenAreaPolicySseAsync(client, query);

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
    public async Task WebSocket_AreaRowPolicy_SubscribeNamedLayer_ReturnsError()
    {
        var fixture = CreateAreaPolicyFixture(PolicyArea);
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
                new Uri("ws://localhost/api/v1/streaming/features?clientLabel=area-policy"), cts.Token);
            _ = await ReceiveWebSocketJsonAsync(socket, cts.Token);

            await SendWebSocketJsonAsync(socket, """{"type":"subscribe","subscriptionId":"area","layerId":0}""", cts.Token);
            var refused = await ReceiveWebSocketJsonAsync(socket, cts.Token);
            refused.GetProperty("type").GetString().Should().Be("error");
            refused.GetProperty("code").GetString().Should().Be("invalid-subscription");
            refused.GetProperty("message").GetString().Should().Contain("Layer 0");

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> OpenAreaPolicySseAsync(HttpClient client, string query)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/streaming/features?{query}");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    private static WebAppFixture CreateAreaPolicyFixture(string? area)
    {
        var rows = Substitute.For<IRlsPolicyStore>();
        rows.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RlsPolicy[]
            {
                new()
                {
                    Role = "*",
                    Service = "*",
                    Layer = "*",
                    Attribute = "NAME",
                    ClaimType = ClaimTypes.Role,
                    AreaGeometry = area,
                },
            });
        return new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IRlsPolicyStore>(rows);
    }
}
