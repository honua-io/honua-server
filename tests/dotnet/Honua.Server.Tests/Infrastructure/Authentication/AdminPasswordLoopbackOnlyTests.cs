// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

// With Authentication:AdminPassword:LoopbackOnly the administrator password works only on a direct connection from the host.
public sealed class AdminPasswordLoopbackOnlyTests
{
    private const string AdminPassword = "LoopbackOnly-AdminPassword123!";

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void ClientConnectionFrom_LoopbackPeerWithoutForwarding_IsDirectLoopback(string peer)
    {
        ClientConnection.From(Request(peer)).Reach.Should().Be(ClientReach.DirectLoopback);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("10.0.4.17")]
    [InlineData("203.0.113.9")]
    [InlineData("::ffff:10.0.4.17")]
    public void ClientConnectionFrom_NetworkPeer_IsNetwork(string peer)
    {
        ClientConnection.From(Request(peer)).Reach.Should().Be(ClientReach.Network);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Original-For", "10.0.4.17:443")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-Host", "honua.example.com")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    [InlineData("X-Original-Proto", "http")]
    [InlineData("X-Original-Host", "localhost")]
    public void ClientConnectionFrom_ProxiedRequestFromLoopback_IsNetwork(string header, string value)
    {
        var context = Request("127.0.0.1");
        context.Request.Headers[header] = value;

        ClientConnection.From(context).Reach.Should().Be(ClientReach.Network);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ClientConnectionFrom_RecordedTransportPeerIsSameLoopback_IsDirectLoopback()
    {
        var context = Request("127.0.0.1");
        context.Items[ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress] = context.Connection.RemoteIpAddress;

        ClientConnection.From(context).Reach.Should().Be(ClientReach.DirectLoopback);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ClientConnectionFrom_LoopbackRewrittenFromAnotherTransportPeer_IsNetwork()
    {
        var context = Request("127.0.0.1");
        context.Items[ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress] = IPAddress.Parse("10.0.4.17");

        ClientConnection.From(context).Reach.Should().Be(ClientReach.Network);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ClientConnectionFrom_LoopbackWithoutTransportPeer_IsNetwork()
    {
        var context = Request("127.0.0.1");
        context.Items[ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress] = null;

        ClientConnection.From(context).Reach.Should().Be(ClientReach.Network);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ClientConnectionFrom_UnknownPeer_IsNetwork()
    {
        ClientConnection.From(new DefaultHttpContext()).Reach.Should().Be(ClientReach.Network);
        ClientConnection.From(null).Reach.Should().Be(ClientReach.Network);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Permits_Default_AcceptsNetworkClient()
    {
        AdminPasswordNetworkPolicy.Permits(new ApiKeyAuthenticationOptions(), ClientReach.Network).Should().BeTrue();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ValidateAdminPasswordAsync_LoopbackOnly_AcceptsDirectLoopback()
    {
        var validation = await ValidateAsync(LoopbackAttempt());

        validation.Rejection.Should().Be(ApiKeyRejection.None);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ValidateAdminPasswordAsync_LoopbackOnly_RefusesNetworkClientAsFailedAttempt()
    {
        var validation = await ValidateAsync(new AdminPasswordAttempt(AdminPassword, "ApiKey", new ClientConnection(IPAddress.Parse("203.0.113.9"), ClientReach.Network)));

        validation.Rejection.Should().Be(ApiKeyRejection.InvalidKey);
        validation.CountsAsFailure.Should().BeTrue();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task PortalVerifier_LoopbackOnly_AcceptsDirectLoopback()
    {
        (await VerifyPortalAsync(Request("127.0.0.1"))).Should().NotBeNull();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task PortalVerifier_LoopbackOnly_RefusesForwardedLoopback()
    {
        var context = Request("127.0.0.1");
        context.Request.Headers["X-Original-For"] = "203.0.113.9:51000";

        (await VerifyPortalAsync(context)).Should().BeNull();
    }

    private static DefaultHttpContext Request(string peer)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        return context;
    }

    private static AdminPasswordAttempt LoopbackAttempt() =>
        new(AdminPassword, "ApiKey", new ClientConnection(IPAddress.Loopback, ClientReach.DirectLoopback));

    private static Task<ApiKeyValidation> ValidateAsync(AdminPasswordAttempt attempt) =>
        ApiKeyAuthenticationHandler.ValidateAdminPasswordAsync(
            attempt,
            new ApiKeyAuthenticationDependencies(Options.Create(LoopbackOnlyOptions())),
            NullLogger.Instance,
            CancellationToken.None);

    private static Task<PortalCredentialPrincipal?> VerifyPortalAsync(HttpContext context) =>
        new AdminPortalCredentialVerifier(
                Options.Create(LoopbackOnlyOptions()),
                logger: NullLogger<AdminPortalCredentialVerifier>.Instance,
                httpContextAccessor: new HttpContextAccessor { HttpContext = context })
            .VerifyAsync("admin", AdminPassword, CancellationToken.None);

    private static ApiKeyAuthenticationOptions LoopbackOnlyOptions() => new()
    {
        AdminPassword = AdminPassword,
        EnvironmentName = "Test",
        AdminPasswordLoopbackOnly = true
    };
}
