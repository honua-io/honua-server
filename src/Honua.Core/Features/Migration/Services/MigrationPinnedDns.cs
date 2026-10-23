// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Product-specific wording for the shared migration outbound HTTP guard.
/// The checks are the same; only the exception text differs.
/// </summary>
/// <param name="HttpOrHttpsMessage">Error when HTTP and HTTPS are both allowed and the scheme is neither.</param>
/// <param name="HttpsOnlyMessage">Error when only HTTPS is allowed and the scheme is not HTTPS.</param>
/// <param name="EmbeddedCredentialsMessage">Error when the URL contains user info.</param>
/// <param name="DisallowedAddressMessage">Error when the host is loopback, private, or unresolvable.</param>
/// <param name="ConnectionFailureMessage">Error when every resolved address refuses the TCP connection.</param>
internal readonly record struct MigrationServiceUrlPolicy(
    string HttpOrHttpsMessage,
    string HttpsOnlyMessage,
    string EmbeddedCredentialsMessage,
    string DisallowedAddressMessage,
    string ConnectionFailureMessage);

/// <summary>
/// Shared HTTPS, redirect, and pinned-DNS checks for migration clients.
/// Each client used to carry its own copy of this guard.
/// </summary>
internal static class MigrationPinnedDns
{
    internal static Uri ValidateServiceUri(string serviceUrl, bool allowUnsafeLocalUrls, MigrationServiceUrlPolicy policy)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (allowUnsafeLocalUrls
                ? uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                : uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException(allowUnsafeLocalUrls
                ? policy.HttpOrHttpsMessage
                : policy.HttpsOnlyMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new HttpRequestException(policy.EmbeddedCredentialsMessage);
        }

        if (!allowUnsafeLocalUrls &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            throw new HttpRequestException(policy.DisallowedAddressMessage);
        }

        return uri;
    }

    internal static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        MigrationServiceUrlPolicy policy,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (!allowUnsafeLocalUrls && OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literalAddress))
            {
                throw new HttpRequestException(policy.DisallowedAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new HttpRequestException(policy.DisallowedAddressMessage);
        }
        catch (ArgumentException)
        {
            throw new HttpRequestException(policy.DisallowedAddressMessage);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(policy.DisallowedAddressMessage);
        }

        if (!allowUnsafeLocalUrls && addresses.Any(static address => OutboundHttpUrlValidator.IsPrivateOrReservedAddress(address)))
        {
            throw new HttpRequestException(policy.DisallowedAddressMessage);
        }

        return addresses;
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        MigrationServiceUrlPolicy policy,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                allowUnsafeLocalUrls,
                policy,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            using var socketOwner = new SocketConnectionOwner(
                new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));

            try
            {
                await socketOwner.Socket
                    .ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return socketOwner.TransferToNetworkStream();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException(policy.ConnectionFailureMessage, lastException);
    }

    internal static Uri? ResolveRedirectUri(Uri requestUri, Uri? location)
    {
        if (location == null)
        {
            return null;
        }

        return location.IsAbsoluteUri
            ? location
            : Uri.TryCreate(requestUri, location, out var redirectUri)
                ? redirectUri
                : null;
    }

    internal static bool IsSafeRedirectUri(Uri requestUri, Uri redirectUri)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp &&
            redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(redirectUri.UserInfo) ||
            !string.Equals(requestUri.DnsSafeHost, redirectUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestUri.Scheme == Uri.UriSchemeHttps && redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (requestUri.Scheme == redirectUri.Scheme)
        {
            return requestUri.Port == redirectUri.Port;
        }

        return requestUri.Scheme == Uri.UriSchemeHttp &&
               redirectUri.Scheme == Uri.UriSchemeHttps &&
               IsDefaultPort(requestUri) &&
               IsDefaultPort(redirectUri);
    }

    private static bool IsDefaultPort(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return uri.Port == 80;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443;
    }
}
