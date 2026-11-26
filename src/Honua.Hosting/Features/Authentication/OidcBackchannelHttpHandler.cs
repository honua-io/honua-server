// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Authentication;

// Backchannel OIDC requests (discovery, JWKS, token exchange) never follow redirects or store cookies,
// and in production pin DNS at connect time to refuse loopback, RFC1918, and cloud metadata addresses.
internal static class OidcBackchannelHttpHandler
{
    internal const int MaxMetadataDocumentBytes = 256 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int ReadChunkSize = 8192;

    private static readonly PinnedDnsFailureMessages PinnedMessages = new(
        "The OIDC destination resolves to a private, loopback, or unresolvable network address, which is not allowed.",
        "Unable to connect to the OIDC destination.");

    private static readonly Lazy<HttpClient> SharedPublicClient = new(() => CreatePublicOnlyClient());
    private static readonly Lazy<HttpClient> SharedPrivateNetworkClient = new(CreateClientAllowingPrivateNetworks);

    internal static bool AllowsPrivateNetworks(IHostEnvironment? environment)
        => environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));

    internal static HttpClient GetSharedClientForEnvironment(IHostEnvironment? environment)
        => AllowsPrivateNetworks(environment)
            ? SharedPrivateNetworkClient.Value
            : SharedPublicClient.Value;

    internal static HttpClient GetSharedPublicOnlyClient() => SharedPublicClient.Value;

    internal static HttpClient GetSharedClientAllowingPrivateNetworks() => SharedPrivateNetworkClient.Value;

    internal static SocketsHttpHandler CreateForEnvironment(IHostEnvironment? environment)
        => AllowsPrivateNetworks(environment)
            ? CreateAllowingPrivateNetworks()
            : CreatePublicOnly();

    internal static SocketsHttpHandler CreateAllowingPrivateNetworks() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    internal static SocketsHttpHandler CreatePublicOnly(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? Dns.GetHostAddressesAsync;
        var handler = new PinnedDnsConnector(resolver, PinnedMessages).CreateHandler(maxConnectionsPerServer: 10);
        handler.UseCookies = false;
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);
        return handler;
    }

    internal static HttpClient CreateClientAllowingPrivateNetworks()
        => CreateWrappedClient(CreateAllowingPrivateNetworks);

    internal static HttpClient CreatePublicOnlyClient(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
        => CreateWrappedClient(() => CreatePublicOnly(hostAddressResolver));

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            return await ReadStreamWithLimitAsync(content, maxBytes, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Timed out reading OIDC response body.", ex);
        }
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
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

    private static HttpClient CreateWrappedClient(Func<SocketsHttpHandler> createHandler)
    {
        var handler = createHandler();
        HttpClient? client = null;
        try
        {
            client = new HttpClient(handler, disposeHandler: true);
            client.Timeout = RequestTimeout;
            return client;
        }
        catch
        {
            if (client is not null)
            {
                client.Dispose();
            }
            else
            {
                handler.Dispose();
            }

            throw;
        }
    }
}
