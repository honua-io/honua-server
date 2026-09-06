// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [Operation(Operations.Query)]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataPoll_RealPortalCredential_RequiresValidSameTenantReplacement(bool expire)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Tenant = "tenant-b" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        fixture.UpdateV2ResourceMetadata(1, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        await using var connection = new NpgsqlConnection(fixture.Postgres.ConnectionString);
        await connection.OpenAsync(ct);
        using var identifiers = new NpgsqlCommandBuilder();
        var schema = identifiers.QuoteIdentifier(fixture.CurrentSchema!);
        await using (var seed = new NpgsqlCommand($$"""
            INSERT INTO {{schema}}.features(objectid, layer_id, attributes)
            VALUES (73011, 0, '{"name":"before-renewal"}'), (73012, 1, '{"name":"tenant-b-secret"}');
            """, connection))
        {
            await seed.ExecuteNonQueryAsync(ct);
        }
        const string referer = "https://odata-auth-proof.example/";
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        async Task<PortalTokenIssuance> IssueAsync(TimeSpan ttl, string? tenant = "tenant-a") => await issuer.IssueAsync(
            new PortalTokenIssueRequest("odata-proof", "OData proof", tenant, ["reader"], PortalTokenClientType.Referer,
                referer, DateTimeOffset.UtcNow + ttl), ct);
        async Task<HttpResponseMessage> PollAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(referer);
            request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");
            return await fixture.Client.SendAsync(request, ct);
        }
        var credential = await IssueAsync(expire ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2));
        using var baselineResponse = await PollAsync("/odata/Features(0)?$filter=ObjectId%20eq%2073011%20or%20ObjectId%20eq%2073012", credential.Token);
        baselineResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var baseline = JsonDocument.Parse(await baselineResponse.Content.ReadAsStringAsync(ct));
        baseline.RootElement.GetProperty("value").GetArrayLength().Should().Be(1);
        baseline.RootElement.GetProperty("value")[0].GetProperty("name").GetString().Should().Be("before-renewal");
        var deltaPath = new Uri(baseline.RootElement.GetProperty("@odata.deltaLink").GetString()!).PathAndQuery;
        if (expire)
        {
            var remaining = credential.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) { await Task.Delay(remaining + TimeSpan.FromMilliseconds(100), ct); }
        }
        else { await issuer.RevokeAsync(credential.Token, ct); }
        using var denied = await PollAsync(deltaPath, credential.Token);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await denied.Content.ReadAsStringAsync(ct)).Should().NotContain("before-renewal").And.NotContain("tenant-b-secret");
        await using (var update = new NpgsqlCommand($$"""
            UPDATE {{schema}}.features SET attributes = '{"name":"after-renewal"}', updated_at = CURRENT_TIMESTAMP
            WHERE layer_id = 0 AND objectid = 73011;
            UPDATE {{schema}}.features SET attributes = '{"name":"tenant-b-secret-after"}', updated_at = CURRENT_TIMESTAMP
            WHERE layer_id = 1 AND objectid = 73012;
            """, connection))
        {
            await update.ExecuteNonQueryAsync(ct);
        }
        using var deniedAfterMutation = await PollAsync(deltaPath, credential.Token);
        deniedAfterMutation.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var replacement = await IssueAsync(TimeSpan.FromMinutes(1));
        using var resumed = await PollAsync(deltaPath, replacement.Token);
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var values = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync(ct));
        values.RootElement.GetProperty("value").GetArrayLength().Should().Be(1);
        values.RootElement.GetProperty("value")[0].GetProperty("ObjectId").GetInt64().Should().Be(73011);
        values.RootElement.GetProperty("value")[0].GetProperty("name").GetString().Should().Be("after-renewal");
        foreach (var tenant in new string?[] { "tenant-b", null })
        {
            var changed = await IssueAsync(TimeSpan.FromMinutes(1), tenant);
            using var hidden = await PollAsync(deltaPath, changed.Token);
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound, "tenant-scoped OData metadata lookup conceals foreign publications");
            (await hidden.Content.ReadAsStringAsync(ct)).Should().NotContain("after-renewal").And.NotContain("tenant-b-secret");
        }
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_RealPortalCredentialExpiresOrIsRevoked_TerminatesAndReplacementResumes(bool webSocket, bool expire)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Tenant = "tenant-b" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        fixture.UpdateV2ResourceMetadata(1, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://stream-proof.example/";
        async Task<PortalTokenIssuance> IssueAsync(TimeSpan ttl, string? tenant = "tenant-a") => await issuer.IssueAsync(
            new PortalTokenIssueRequest("stream-proof", "Stream proof", tenant, ["reader"],
                PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow + ttl), ct);
        var anchor = await fixture.GetService<IFeatureChangeEventStore>().AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 73000,
            Operation = "update",
            Protocol = "rest",
            RequestId = "auth-anchor"
        }, ct);
        var credential = await IssueAsync(expire ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(1));
        var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
        async Task PublishAsync(long id)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 1,
                ObjectId = id + 100,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"tenant-b-{id}",
                PropertiesJson = "{\"name\":\"tenant-b-secret\"}"
            }, ct);
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 0,
                ObjectId = id,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"auth-proof-{id}",
                PropertiesJson = "{\"name\":\"credential-proof\"}"
            }, ct);
        }

        string Path(string token, long? cursor = null) =>
            $"/api/v1/streaming/features?serviceId=test&layers=0&token={token}" + (cursor.HasValue ? $"&cursor={cursor}" : "");
        async Task<WebSocket> ConnectAsync(string token, long? cursor = null)
        {
            var client = fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request => { configure?.Invoke(request); request.Headers.Referer = referer; };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token, cursor)), ct);
        }

        long lastCursor;
        if (webSocket)
        {
            using var socket = await ConnectAsync(credential.Token, anchor.Cursor);
            (await ReceiveWebSocketJsonAsync(socket, ct)).GetRawText().Should().NotContain("tenant-b");
            await PublishAsync(73001);
            JsonElement frame;
            do { frame = await ReceiveWebSocketJsonAsync(socket, ct); frame.GetRawText().Should().NotContain("tenant-b-secret"); }
            while (frame.GetProperty("type").GetString() != "feature-change");
            frame.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(5));
            var buffer = new byte[8192];
            WebSocketReceiveResult terminal;
            do
            {
                terminal = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), bound.Token);
                if (terminal.MessageType == WebSocketMessageType.Text)
                {
                    Encoding.UTF8.GetString(buffer, 0, terminal.Count).Should().NotContain("feature-change");
                }
            } while (terminal.MessageType != WebSocketMessageType.Close);
            terminal.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
            terminal.CloseStatusDescription.Should().Be("authorization-ended");
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token, anchor.Cursor));
            request.Headers.Referrer = new Uri(referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            (await ReadNextSseEventAsync(reader, ct)).Data.GetRawText().Should().NotContain("tenant-b");
            await PublishAsync(73001);
            SseEvent frame;
            do { frame = await ReadNextSseEventAsync(reader, ct); frame.Data.GetRawText().Should().NotContain("tenant-b-secret"); } while (frame.EventName != "feature-change");
            frame.Data.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.Data.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(5));
            do
            {
                frame = await ReadNextSseEventAsync(reader, bound.Token);
                frame.EventName.Should().NotBe("feature-change");
            } while (!frame.Data.TryGetProperty("code", out _));
            frame.Data.GetProperty("code").GetString().Should().Be("authorization-ended");
            (await reader.ReadLineAsync(bound.Token)).Should().BeNull("the authorization outcome is terminal");
        }

        await PublishAsync(73002);
        var replacement = await IssueAsync(TimeSpan.FromMinutes(1));
        using var resumed = await ConnectAsync(replacement.Token, lastCursor);
        JsonElement replay;
        do { replay = await ReceiveWebSocketJsonAsync(resumed, ct); }
        while (replay.GetProperty("type").GetString() != "feature-change");
        replay.GetProperty("objectId").GetInt64().Should().Be(73002);
        replay.GetProperty("cursor").GetInt64().Should().BeGreaterThan(lastCursor);

        using var rejected = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token, lastCursor));
        rejected.Headers.Referrer = new Uri(referer);
        rejected.Headers.Accept.ParseAdd("text/event-stream");
        using var denied = await fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var tenant in new string?[] { "tenant-b", null })
        {
            var changed = await IssueAsync(TimeSpan.FromMinutes(1), tenant);
            using var incompatible = new HttpRequestMessage(HttpMethod.Get, Path(changed.Token, lastCursor));
            incompatible.Headers.Referrer = new Uri(referer);
            incompatible.Headers.Accept.ParseAdd("text/event-stream");
            using var hidden = await fixture.Client.SendAsync(incompatible, HttpCompletionOption.ResponseHeadersRead, ct);
            hidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an authenticated principal cannot access another tenant's protected layer");
            (await hidden.Content.ReadAsStringAsync(ct)).Should().NotContain("credential-proof").And.NotContain("tenant-b-secret");
        }
    }
}
