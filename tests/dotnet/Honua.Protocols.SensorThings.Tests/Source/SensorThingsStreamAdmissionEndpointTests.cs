// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// End-to-end admission for <c>GET /sta/v1.1/ObservationsStream</c> (#4198). The host is
/// configured with small caps (2 per principal, 3 per tenant, 4 per node) so every limit is
/// reachable with real credentials, and each refusal is checked for its status, Retry-After
/// hint and problem body, and for never opening a stream.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsStreamAdmissionEndpointTests : IAsyncLifetime
{
    private const string Route = "/sta/v1.1/ObservationsStream";
    private const string Referer = "https://observation-admission.example/";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MultiTenancy:MultiTenantAdminRoles:0"] = "admin",
                ["SensorThings:Streaming:MaxSessionsPerPrincipal"] = "2",
                ["SensorThings:Streaming:MaxSessionsPerTenant"] = "3",
                ["SensorThings:Streaming:MaxConcurrentSessions"] = "4",
                ["SensorThings:Streaming:RetryAfterSeconds"] = "7"
            }));
    });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_CapsPerPrincipalTenantAndNode_RefuseWithoutLockingOthersOut()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var open = new List<HttpResponseMessage>();
        try
        {
            // One credential (the admin API key, scoped to tenant-a) fills its own quota of 2.
            using var admin = _fixture.CreateAdminClient();
            admin.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-a");
            open.Add(await OpenStreamAsync(admin, Route, ct));
            open.Add(await OpenStreamAsync(admin, Route, ct));

            using (var overQuota = await SendStreamRequestAsync(admin, Route, ct))
            {
                await AssertRefusedAsync(overQuota, HttpStatusCode.TooManyRequests, "2 concurrent sessions per principal", ct);
            }

            // The WebSocket transport is refused by the same admission check, before the upgrade.
            var socketClient = _fixture.CreateWebSocketClient();
            socketClient.ConfigureRequest = request =>
            {
                request.Headers["X-API-Key"] = WebAppFixture.SharedAdminPassword;
                request.Headers["X-Honua-Test-Schema"] = _fixture.CurrentSchema;
                request.Headers["X-Honua-Tenant"] = "tenant-a";
            };
            var upgrade = () => socketClient.ConnectAsync(new Uri("ws://localhost" + Route), ct);
            (await upgrade.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("429");

            // A different principal in the same tenant is still admitted: the saturated
            // credential cannot lock other subscribers out (the #4198 defect).
            open.Add(await OpenStreamAsync(_fixture.Client, await PortalRouteAsync("sta-admission-1", "tenant-a", ct), ct, Referer));

            // tenant-a now holds 3 of 3: a fresh principal there is refused by the tenant cap.
            using (var tenantFull = await SendStreamRequestAsync(
                _fixture.Client, await PortalRouteAsync("sta-admission-2", "tenant-a", ct), ct, Referer))
            {
                await AssertRefusedAsync(tenantFull, HttpStatusCode.TooManyRequests, "3 concurrent sessions per tenant", ct);
            }

            // Another tenant is unaffected by tenant-a's saturation and takes the last node slot.
            open.Add(await OpenStreamAsync(_fixture.Client, await PortalRouteAsync("sta-admission-3", "tenant-b", ct), ct, Referer));

            // The node holds 4 of 4: an idle principal in an idle tenant gets 503, not 429.
            using var tenantC = _fixture.CreateAdminClient();
            tenantC.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-c");
            using (var nodeFull = await SendStreamRequestAsync(tenantC, Route, ct))
            {
                await AssertRefusedAsync(nodeFull, HttpStatusCode.ServiceUnavailable, "capacity on this node is exhausted", ct);
            }

            // Closing one of the admin's own streams releases its slot; the admin is re-admitted.
            open[0].Dispose();
            open.RemoveAt(0);
            open.Add(await OpenStreamWhenReleasedAsync(admin, ct));
        }
        finally
        {
            open.ForEach(response => response.Dispose());
        }
    }

    private async Task<string> PortalRouteAsync(string clientId, string tenant, CancellationToken ct)
    {
        var credential = await _fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(clientId, clientId, tenant, ["reader"], PortalTokenClientType.Referer,
                Referer, DateTimeOffset.UtcNow.AddMinutes(5)), ct);
        return $"{Route}?token={credential.Token}";
    }

    private static async Task<HttpResponseMessage> SendStreamRequestAsync(
        HttpClient client, string path, CancellationToken ct, string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (referer is not null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<HttpResponseMessage> OpenStreamAsync(
        HttpClient client, string path, CancellationToken ct, string? referer = null)
    {
        var response = await SendStreamRequestAsync(client, path, ct, referer);
        try
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            (await reader.ReadLineAsync(ct)).Should().Be("event: status");
            (await reader.ReadLineAsync(ct)).Should().Contain("\"connected\"");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> OpenStreamWhenReleasedAsync(HttpClient client, CancellationToken ct)
    {
        // Disconnect is observed by the server asynchronously; the slot is released once the
        // request aborts. Anything other than admission or a principal-cap refusal is a failure.
        while (true)
        {
            var response = await SendStreamRequestAsync(client, Route, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                response.Dispose();
                return await OpenStreamAsync(client, Route, ct);
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    private static async Task AssertRefusedAsync(
        HttpResponseMessage response, HttpStatusCode expected, string detail, CancellationToken ct)
    {
        response.StatusCode.Should().Be(expected);
        response.Headers.RetryAfter.Should().NotBeNull("a refusal must tell the client when to retry");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(7));
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
        var body = await response.Content.ReadAsStringAsync(ct);
        body.Should().Contain(detail).And.NotContain("connected");
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
