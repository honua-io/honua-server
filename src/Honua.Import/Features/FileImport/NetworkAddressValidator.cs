// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Import.FileImport;

internal static class NetworkAddressValidator
{
    public static async Task<bool> IsDisallowedAddressAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
        => (await ValidateAndResolveAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false)).IsDisallowed;

    /// <summary>
    /// Validates the URI's host address and returns the resolved IP addresses for use by
    /// callers that want to pin the connection (PA-154 DNS rebinding mitigation). Returns
    /// <c>true</c> and an empty array when the address is disallowed, or <c>false</c> and
    /// the resolved non-private IP addresses when the host is permitted.
    /// <para>
    /// Callers should use the returned addresses with <see cref="PinnedDnsHttpMessageHandler"/>
    /// so the TCP connection is made to the address that was validated rather than letting
    /// the HttpClient re-resolve DNS independently (TOCTOU gap).
    /// </para>
    /// </summary>
    internal static async Task<(bool IsDisallowed, IPAddress[] ResolvedAddresses)> ValidateAndResolveAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (uri.IsLoopback || OutboundHttpUrlValidator.IsLocalhostHostName(uri.Host))
        {
            return (IsDisallowed: true, ResolvedAddresses: []);
        }

        // Literal IP: validate in place; no DNS resolution needed.
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literalAddress)
                ? (IsDisallowed: true, ResolvedAddresses: [])
                : (IsDisallowed: false, ResolvedAddresses: [literalAddress]);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return (IsDisallowed: true, ResolvedAddresses: []);
        }
        catch (ArgumentException)
        {
            return (IsDisallowed: true, ResolvedAddresses: []);
        }

        if (addresses.Length == 0)
        {
            return (IsDisallowed: true, ResolvedAddresses: []);
        }

        if (addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress))
        {
            return (IsDisallowed: true, ResolvedAddresses: []);
        }

        return (IsDisallowed: false, ResolvedAddresses: addresses);
    }
}
