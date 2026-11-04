// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Import.FileImport;

internal static class NetworkAddressValidator
{
    public static async Task<bool> IsDisallowedAddressAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
        => (await ValidateAndResolveAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false)).IsDisallowed;

    /// <summary>
    /// Returns whether the URI's host is disallowed and, when it is allowed, the addresses it resolved to.
    /// </summary>
    internal static async Task<(bool IsDisallowed, IPAddress[] ResolvedAddresses)> ValidateAndResolveAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        var allowed = uri.IsLoopback
            ? null
            : await PinnedDnsConnector.TryResolveAllowedAddressesAsync(uri.DnsSafeHost, hostAddressResolver, cancellationToken)
                .ConfigureAwait(false);
        return allowed is null ? (IsDisallowed: true, ResolvedAddresses: []) : (IsDisallowed: false, ResolvedAddresses: allowed);
    }
}
