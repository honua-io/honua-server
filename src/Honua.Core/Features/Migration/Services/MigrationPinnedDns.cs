// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Features.Migration.Services;

internal readonly record struct MigrationServiceUrlPolicy(
    string HttpOrHttpsMessage,
    string HttpsOnlyMessage,
    string EmbeddedCredentialsMessage,
    string DisallowedAddressMessage,
    string ConnectionFailureMessage);

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
        var addresses = allowUnsafeLocalUrls
            ? await ResolveAnyAddressesAsync(host, hostAddressResolver, cancellationToken).ConfigureAwait(false)
            : await PinnedDnsConnector.TryResolveAllowedAddressesAsync(host, hostAddressResolver, cancellationToken).ConfigureAwait(false);
        return addresses ?? throw new HttpRequestException(policy.DisallowedAddressMessage);
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

        var connector = new PinnedDnsConnector(
            hostAddressResolver,
            new PinnedDnsFailureMessages(policy.DisallowedAddressMessage, policy.ConnectionFailureMessage));
        return await connector.ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IPAddress[]?> ResolveAnyAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return [literalAddress];
        }

        try
        {
            var addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length == 0 ? null : addresses;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
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
