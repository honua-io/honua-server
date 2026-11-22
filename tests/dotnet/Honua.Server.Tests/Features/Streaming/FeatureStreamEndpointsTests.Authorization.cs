// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
            builder.UseSetting("MultiTenancy:Enabled", "true");
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
        async Task<ExpiringCredential> IssueAsync(TimeSpan ttl, string? tenant = "tenant-a") => ExpiringCredential.From(await issuer.IssueAsync(
            new PortalTokenIssueRequest("odata-proof", "OData proof", tenant, ["reader"], PortalTokenClientType.Referer,
                referer, DateTimeOffset.UtcNow + ttl), ct));
        async Task<HttpResponseMessage> PollAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(referer);
            request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");
            return await fixture.Client.SendAsync(request, ct);
        }
        const string baselinePath = "/odata/Features(0)?$filter=ObjectId%20eq%2073011%20or%20ObjectId%20eq%2073012";
        using (var warm = await PollAsync(baselinePath, (await IssueAsync(TimeSpan.FromMinutes(1))).Token))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.OK, "the warm-up keeps startup cost off the credential under test");
        }

        var started = await ExpiringCredentials.IssueForSetupAsync(
            expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable,
            ttl => IssueAsync(ttl),
            issued => PollAsync(baselinePath, issued.Token));
        var credential = started.Credential;
        using var baselineResponse = started.Result;
        baselineResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var baseline = JsonDocument.Parse(await baselineResponse.Content.ReadAsStringAsync(ct));
        baseline.RootElement.GetProperty("value").GetArrayLength().Should().Be(1);
        baseline.RootElement.GetProperty("value")[0].GetProperty("name").GetString().Should().Be("before-renewal");
        var deltaPath = new Uri(baseline.RootElement.GetProperty("@odata.deltaLink").GetString()!).PathAndQuery;
        if (expire)
        {
            await ExpiringCredentials.WaitPastExpiryAsync(credential, ct);
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
    [Operation(Operations.Query)]
    [InlineData("anonymous", false)]
    [InlineData("invalid", false)]
    [InlineData("expired", false)]
    [InlineData("revoked", false)]
    [InlineData("anonymous", true)]
    [InlineData("invalid", true)]
    [InlineData("expired", true)]
    [InlineData("revoked", true)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataRead_TenantScopedProtectedLayerUnderDefaultTenant_ChallengesMissingOrEndedCredential(
        string credentialState, bool candidateAuthentication)
    {
        // Production leaves MultiTenancy:DefaultTenantId at "public", so a request without a
        // valid credential resolves a tenant that cannot see tenant-a's layer. That is still an
        // authentication failure, not a missing layer.
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("MultiTenancy:Enabled", "true");
            builder.UseSetting("MultiTenancy:DefaultTenantId", "public");
            if (candidateAuthentication)
            {
                // The receipt candidate also validates OIDC bearers, so every bearer request is
                // routed through the composite scheme before the portal-token bridge sees it.
                const string issuer = "https://live-authorization-issuer.example";
                const string audience = "live-authorization-candidate";
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", issuer);
                builder.UseSetting("Oidc:Generic:ClientId", audience);
                builder.UseSetting("Oidc:Generic:ClientSecret", "unused-static-key-issuer");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "LiveAuthorizationCandidateSigningKeyForHS256Only!");
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidIssuers:0", issuer);
                builder.UseSetting("Oidc:TokenValidation:ValidAudiences:0", audience);
                builder.UseSetting("Authentication:PortalCredentialVerifier:UseOidc", "true");
            }
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        await using (var connection = new NpgsqlConnection(fixture.Postgres.ConnectionString))
        {
            await connection.OpenAsync(ct);
            using var identifiers = new NpgsqlCommandBuilder();
            await using var seed = new NpgsqlCommand($$"""
                INSERT INTO {{identifiers.QuoteIdentifier(fixture.CurrentSchema!)}}.features(objectid, layer_id, attributes)
                VALUES (73021, 0, '{"name":"tenant-a-secret"}');
                """, connection);
            await seed.ExecuteNonQueryAsync(ct);
        }
        const string referer = "https://odata-default-tenant-proof.example/";
        const string path = "/odata/Features(0)?$filter=ObjectId%20eq%2073021";
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        async Task<ExpiringCredential> IssueAsync(TimeSpan ttl, string tenant) => ExpiringCredential.From(await issuer.IssueAsync(
            new PortalTokenIssueRequest("odata-default-tenant", "OData proof", tenant, ["reader"], PortalTokenClientType.Referer,
                referer, DateTimeOffset.UtcNow + ttl), ct));
        async Task<HttpResponseMessage> ReadAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            request.Headers.Referrer = new Uri(referer);
            return await fixture.Client.SendAsync(request, ct);
        }

        // The positive control also warms the host, so a short-lived credential below is not
        // spent on the first request's startup cost.
        using (var admitted = await ReadAsync((await IssueAsync(TimeSpan.FromMinutes(1), "tenant-a")).Token))
        {
            admitted.StatusCode.Should().Be(HttpStatusCode.OK, "the tenant-a reader is the positive control");
            (await admitted.Content.ReadAsStringAsync(ct)).Should().Contain("tenant-a-secret");
        }

        var started = await ExpiringCredentials.IssueForSetupAsync(
            credentialState == "expired" ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable,
            ttl => IssueAsync(ttl, "tenant-a"),
            issued => ReadAsync(issued.Token));
        var credential = started.Credential;
        using (var admitted = started.Result)
        {
            admitted.StatusCode.Should().Be(HttpStatusCode.OK, "the credential under test is valid before its boundary");
        }

        string? presented = credentialState switch
        {
            "anonymous" => null,
            "invalid" => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
            _ => credential.Token,
        };
        if (credentialState == "expired")
        {
            await ExpiringCredentials.WaitPastExpiryAsync(credential, ct);
        }
        else if (credentialState == "revoked")
        {
            await issuer.RevokeAsync(credential.Token, ct);
        }

        using var denied = await ReadAsync(presented);
        var deniedBody = await denied.Content.ReadAsStringAsync(ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, deniedBody);
        denied.Headers.WwwAuthenticate.Should().NotBeEmpty("a 401 names the credential the client must present");
        if (presented is not null)
        {
            denied.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme).Should().Contain("Bearer");
        }
        deniedBody.Should().NotContain("tenant-a-secret");

        var foreign = await IssueAsync(TimeSpan.FromMinutes(1), "tenant-b");
        using var concealed = await ReadAsync(foreign.Token);
        concealed.StatusCode.Should().Be(HttpStatusCode.NotFound, "an authenticated principal of another tenant keeps tenant concealment");
        (await concealed.Content.ReadAsStringAsync(ct)).Should().NotContain("tenant-a-secret");
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
            builder.UseSetting("MultiTenancy:Enabled", "true");
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Tenant = "tenant-b" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        fixture.UpdateV2ResourceMetadata(1, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var anchor = await fixture.AppendChangeAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 73000,
            Operation = "update",
            Protocol = "rest",
            RequestId = "auth-anchor"
        }, ct);
        var proof = new CredentialStreamProof(fixture, anchor.Cursor, ct);

        // Warm the stream path first so a short-lived credential is not spent on startup cost.
        using (var warm = await proof.ConnectAsync((await proof.IssueAsync(TimeSpan.FromMinutes(1))).Token, anchor.Cursor))
        {
            (await ReceiveWebSocketJsonAsync(warm, ct)).GetRawText().Should().NotContain("tenant-b");
        }


        ExpiringCredential credential;
        if (webSocket)
        {
            var started = await ExpiringCredentials.IssueForSetupAsync(
                expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable,
                ttl => proof.IssueAsync(ttl),
                proof.SubscribeWebSocketAsync);
            credential = started.Credential;
            using var socket = started.Result;
            proof.AssertEverySetupFrameWasClean();
            if (!expire) { await proof.RevokeAsync(credential); }
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
            var started = await ExpiringCredentials.IssueForSetupAsync(
                expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable, ttl => proof.IssueAsync(ttl), proof.SubscribeSseAsync);
            credential = started.Credential;
            using var response = started.Result;
            using var reader = proof.SseReader!;
            proof.AssertEverySetupFrameWasClean();
            SseEvent frame;
            if (!expire) { await proof.RevokeAsync(credential); }
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

        await proof.PublishAsync(73002);
        var replacement = await proof.IssueAsync(TimeSpan.FromMinutes(1));
        using var resumed = await proof.ConnectAsync(replacement.Token, proof.LastCursor);
        JsonElement replay;
        do { replay = await ReceiveWebSocketJsonAsync(resumed, ct); }
        while (replay.GetProperty("type").GetString() != "feature-change");
        replay.GetProperty("objectId").GetInt64().Should().Be(73002);
        replay.GetProperty("cursor").GetInt64().Should().BeGreaterThan(proof.LastCursor);

        using var rejected = CredentialStreamProof.SseRequest(credential.Token, proof.LastCursor);
        using var denied = await fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an expired or revoked credential has no tenant binding and cannot see the tenant-owned layer");

        foreach (var tenant in new string?[] { "tenant-b", null })
        {
            var changed = await proof.IssueAsync(TimeSpan.FromMinutes(1), tenant);
            using var incompatible = CredentialStreamProof.SseRequest(changed.Token, proof.LastCursor);
            using var hidden = await fixture.Client.SendAsync(incompatible, HttpCompletionOption.ResponseHeadersRead, ct);
            hidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an authenticated principal cannot access another tenant's protected layer");
            (await hidden.Content.ReadAsStringAsync(ct)).Should().NotContain("credential-proof").And.NotContain("tenant-b-secret");
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_OnKestrel_CredentialExpiresOrIsRevoked_ClosesAuthorizationEndedBeforeTransportEnds(bool expire)
    {
        // TestServer keeps the close frame even when the endpoint's pending receive is
        // cancelled; Kestrel tears the socket down and the client saw 1006 instead of the
        // typed close. Only a real Kestrel host proves this outcome.
        await using var fixture = new WebAppFixture().UseKestrel().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("MultiTenancy:Enabled", "true");
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://kestrel-stream-proof.example/";
        var server = fixture.Client.BaseAddress!;
        server.IsLoopback.Should().BeTrue("the fixture must be serving through loopback Kestrel, not TestServer");
        async Task<ExpiringCredential> IssueAsync(TimeSpan ttl) => ExpiringCredential.From(await issuer.IssueAsync(new PortalTokenIssueRequest(
            "kestrel-proof", "Kestrel proof", "tenant-a", ["reader"], PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow + ttl), ct));
        async Task<ClientWebSocket> ConnectAsync(string token)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Referer", referer);
            foreach (var header in fixture.Client.DefaultRequestHeaders)
            {
                socket.Options.SetRequestHeader(header.Key, string.Join(",", header.Value));
            }
            await socket.ConnectAsync(new UriBuilder(server)
            {
                Scheme = "ws",
                Path = "/api/v1/streaming/features",
                Query = $"serviceId=test&layers=0&token={token}",
            }.Uri, ct);
            return socket;
        }

        // Warm the stream path first so a short-lived credential is not spent on startup cost.
        using (var warm = await ConnectAsync((await IssueAsync(TimeSpan.FromMinutes(1))).Token))
        {
            await warm.CloseAsync(WebSocketCloseStatus.NormalClosure, "warm-up", ct);
        }

        // The candidate dropped the close frame on three of four runs; repeat so a race cannot pass by luck.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var started = await ExpiringCredentials.IssueForSetupAsync(
                expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable,
                ttl => IssueAsync(ttl),
                issued => ConnectAsync(issued.Token));
            var credential = started.Credential;
            using var socket = started.Result;

            DateTimeOffset boundary;
            if (expire)
            {
                boundary = credential.ExpiresAt;
            }
            else
            {
                await issuer.RevokeAsync(credential.Token, ct);
                boundary = DateTimeOffset.UtcNow;
            }

            var buffer = new byte[64 * 1024];
            WebSocketReceiveResult received;
            try
            {
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                } while (received.MessageType != WebSocketMessageType.Close);
            }
            catch (WebSocketException error)
            {
                throw new Xunit.Sdk.XunitException(
                    $"attempt {attempt}: the transport ended without the authorization close frame ({error.WebSocketErrorCode}: {error.Message})");
            }

            var terminatedAt = DateTimeOffset.UtcNow;
            received.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation, $"attempt {attempt}");
            received.CloseStatusDescription.Should().Be("authorization-ended", $"attempt {attempt}");
            (terminatedAt - boundary).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5), $"attempt {attempt}: the documented bound");
            if (expire)
            {
                terminatedAt.Should().BeOnOrAfter(credential.ExpiresAt, $"attempt {attempt}: authorization cannot end before the advertised expiry");
            }
        }
    }
}
