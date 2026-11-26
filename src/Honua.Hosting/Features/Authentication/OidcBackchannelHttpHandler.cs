// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Authentication;

internal readonly record struct OidcBackchannelPolicy(
    bool RequireHttps,
    bool AllowPrivateNetworks = false,
    Func<string, CancellationToken, Task<IPAddress[]>>? HostAddressResolver = null);

// Backchannel OIDC requests (discovery, JWKS, token exchange) never follow redirects or store cookies,
// and in production pin DNS at connect time to refuse loopback, RFC1918, and cloud metadata addresses.
internal static class OidcBackchannelHttpHandler
{
    internal const int MaxMetadataDocumentBytes = 256 * 1024;
    private const int ReadChunkSize = 8192;

    private static readonly PinnedDnsFailureMessages PinnedMessages = new(
        "The OIDC destination resolves to a private, loopback, or unresolvable network address, which is not allowed.",
        "Unable to connect to the OIDC destination.");

    internal static bool AllowsPrivateNetworks(IHostEnvironment? environment)
        => environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));

    internal static SocketsHttpHandler Create(IHostEnvironment? environment)
        => Create(AllowsPrivateNetworks(environment));

    internal static SocketsHttpHandler Create(
        bool allowPrivateNetworks,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        if (allowPrivateNetworks)
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
        }

        var resolver = hostAddressResolver ?? Dns.GetHostAddressesAsync;
        var handler = new PinnedDnsConnector(resolver, PinnedMessages).CreateHandler(maxConnectionsPerServer: 10);
        handler.UseCookies = false;
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);
        return handler;
    }

    internal static HttpClient CreateClient(
        bool allowPrivateNetworks,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
        => new(Create(allowPrivateNetworks, hostAddressResolver), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

    internal static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidOperationException("OIDC response body exceeds the maximum allowed size.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(ReadChunkSize, maxBytes + 1)];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("OIDC response body exceeds the maximum allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
