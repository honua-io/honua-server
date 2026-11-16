// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Infrastructure.Authentication;

internal enum ClientReach
{
    Network,

    // A loopback peer that carried no forwarding header: the host itself.
    DirectLoopback
}

internal readonly record struct ClientConnection(IPAddress? Address, ClientReach Reach)
{
    // Headers that mean the request came through a proxy, before or after forwarded-header processing.
    private static readonly string[] ForwardingHeaders =
    [
        "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Real-IP", "Forwarded",
        "X-Original-For", "X-Original-Proto", "X-Original-Host"
    ];

    // The connection address is rewritten from forwarded headers, so a proxied request never
    // counts as direct loopback even when its address reads as loopback.
    internal static ClientConnection From(HttpContext? context)
    {
        var peer = context?.Connection.RemoteIpAddress;
        if (peer is null)
        {
            return default;
        }

        var direct = IsLoopback(peer) && TransportPeerIs(context!, peer) &&
            !ForwardingHeaders.Any(context!.Request.Headers.ContainsKey);
        return new ClientConnection(peer, direct ? ClientReach.DirectLoopback : ClientReach.Network);
    }

    // With forwarded headers on, the server records the transport peer before rewriting it; a listener
    // without a peer address (a Unix socket) records none and is never direct loopback.
    private static bool TransportPeerIs(HttpContext context, IPAddress peer) =>
        !context.Items.TryGetValue(ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress, out var recorded) ||
        (recorded is IPAddress original && IsLoopback(original) && original.Equals(peer));

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

internal static class AdminPasswordNetworkPolicy
{
    internal const string LoopbackOnlyKey = "Authentication:AdminPassword:LoopbackOnly";

    internal static bool Permits(ApiKeyAuthenticationOptions options, ClientReach reach)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !options.AdminPasswordLoopbackOnly || reach == ClientReach.DirectLoopback;
    }
}
