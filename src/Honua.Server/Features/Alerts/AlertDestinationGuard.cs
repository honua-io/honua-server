// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Alerts;

internal readonly record struct AlertDestinationCheck(Uri? Uri, bool Retryable, string? Error)
{
    [MemberNotNullWhen(true, nameof(Uri))]
    public bool IsAllowed => Uri is not null;

    public static AlertDestinationCheck Allowed(Uri uri) => new(uri, false, null);

    public static AlertDestinationCheck Blocked(bool retryable, string error) => new(null, retryable, error);
}

// A disallowed destination is not retried; a DNS failure is retryable, and either failure blocks the send.
internal sealed class AlertDestinationGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _hostAddressResolver;

    public AlertDestinationGuard(Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _hostAddressResolver = hostAddressResolver;
    }

    public async Task<AlertDestinationCheck> CheckAsync(
        string destination,
        string subject,
        CancellationToken cancellationToken)
    {
        var validation = _hostAddressResolver is null
            ? await OutboundHttpUrlValidator
                .ValidateAsync(destination, cancellationToken)
                .ConfigureAwait(false)
            : await OutboundHttpUrlValidator
                .ValidateAsync(destination, _hostAddressResolver, cancellationToken)
                .ConfigureAwait(false);

        if (validation.IsValid && validation.Uri is not null)
        {
            return AlertDestinationCheck.Allowed(validation.Uri);
        }

        // Blocked either way. Only the retry classification differs, and the validator's own message
        // already tells an operator whether the destination was rejected or merely unverifiable.
        return AlertDestinationCheck.Blocked(
            retryable: validation.IsHostResolutionUnavailable,
            $"{subject} {validation.ErrorMessage ?? "must be a valid HTTPS URL."}");
    }
}
