// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.Import.FileImport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that pins outbound TCP connections to a pre-resolved
/// set of IP addresses, eliminating the DNS-rebinding TOCTOU gap that exists when the
/// <see cref="NetworkAddressValidator"/> resolves DNS at validation time and the underlying
/// <see cref="System.Net.Http.HttpClient"/> independently resolves it again at connection time.
/// <para>
/// Usage pattern:
/// <code>
/// var (isDisallowed, pinnedAddresses) = await NetworkAddressValidator.ValidateAndResolveAsync(
///     uri, hostAddressResolver, cancellationToken);
/// if (isDisallowed) { /* reject */ }
/// using var handler = new PinnedDnsHttpMessageHandler(uri.Host, pinnedAddresses);
/// using var client = new HttpClient(handler);
/// // client now connects to pinnedAddresses, not a freshly resolved DNS result.
/// </code>
/// </para>
/// </summary>
internal sealed class PinnedDnsHttpMessageHandler : DelegatingHandler
{
    private readonly string _host;
    private readonly IPAddress[] _pinnedAddresses;

    /// <summary>
    /// Initialises the handler.
    /// </summary>
    /// <param name="host">The hostname that will appear in the request URI.</param>
    /// <param name="pinnedAddresses">
    /// The IP addresses to connect to.  These should be the addresses returned by
    /// <see cref="NetworkAddressValidator.ValidateAndResolveAsync"/> immediately before
    /// this handler is created so the validation-to-connection window is as short as possible.
    /// </param>
    public PinnedDnsHttpMessageHandler(string host, IPAddress[] pinnedAddresses)
        : base(BuildInnerHandler(host, pinnedAddresses))
    {
        _host = host;
        _pinnedAddresses = pinnedAddresses;
    }

    /// <summary>Gets the hostname this handler is pinned to.</summary>
    internal string Host => _host;

    /// <summary>Gets the pinned addresses.</summary>
    internal IReadOnlyList<IPAddress> PinnedAddresses => _pinnedAddresses;

    private static SocketsHttpHandler BuildInnerHandler(string host, IPAddress[] pinnedAddresses)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(pinnedAddresses);

        if (pinnedAddresses.Length == 0)
        {
            throw new ArgumentException("At least one pinned address is required.", nameof(pinnedAddresses));
        }

        var inner = new SocketsHttpHandler
        {
            // PA-154: ConnectCallback pins the connection to the already-validated IP address(es).
            // This ensures that the IP which passed the private-address check is the one actually
            // connected to, preventing a DNS rebinding attack where a TTL-0 record could swap to a
            // cloud metadata address (e.g. 169.254.169.254) between the validation check and the
            // TCP connection.
            ConnectCallback = async (context, ct) =>
            {
                // Round-robin across the pinned set for basic resilience.
                // In practice migration calls are short-lived and a single address is typical.
                var address = pinnedAddresses[Environment.TickCount % pinnedAddresses.Length];
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    var port = context.DnsEndPoint.Port;
                    await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return inner;
    }
}
