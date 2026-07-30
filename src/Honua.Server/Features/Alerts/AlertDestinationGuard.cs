// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Alerts;

/// <summary>
/// Outcome of an <see cref="AlertDestinationGuard"/> check: either an admitted destination URI, or
/// a block with the retry classification the delivery pipeline should record for the attempt.
/// </summary>
/// <param name="Uri">The vetted destination when the check passed; otherwise <see langword="null"/>.</param>
/// <param name="Retryable">Whether a blocked attempt should be retried (bounded by the dispatch item's MaxAttempts).</param>
/// <param name="Error">Operator-facing failure text when the destination was blocked.</param>
internal readonly record struct AlertDestinationCheck(Uri? Uri, bool Retryable, string? Error)
{
    /// <summary>
    /// Whether the destination was admitted and may be contacted.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Uri))]
    public bool IsAllowed => Uri is not null;

    /// <summary>
    /// Creates an admitted result for a vetted destination.
    /// </summary>
    public static AlertDestinationCheck Allowed(Uri uri) => new(uri, false, null);

    /// <summary>
    /// Creates a blocked result carrying the retry classification and operator-facing message.
    /// </summary>
    public static AlertDestinationCheck Blocked(bool retryable, string error) => new(null, retryable, error);
}

/// <summary>
/// Single place where every alert delivery sink vets an outbound destination and maps the result
/// onto a retry classification. The SSRF posture is unchanged and still fails closed — a
/// destination that cannot be vetted is never contacted — but the two very different reasons a
/// check can fail are no longer conflated:
/// <list type="bullet">
///   <item><description>a genuinely disallowed destination (private/loopback address, non-HTTPS
///   scheme, embedded credentials) is permanent, so the attempt is non-retryable and dead-letters
///   immediately;</description></item>
///   <item><description>a host name resolution failure is indeterminate and usually transient, so
///   the attempt is retryable — the send is still blocked, and the existing dispatch
///   <c>MaxAttempts</c>/backoff policy bounds the retries so a permanently broken resolver still
///   dead-letters (#3057).</description></item>
/// </list>
/// The host-address resolver is injectable so sinks can be tested without live DNS (#3056).
/// </summary>
internal sealed class AlertDestinationGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _hostAddressResolver;

    /// <summary>
    /// Creates the guard. When <paramref name="hostAddressResolver"/> is <see langword="null"/> the
    /// validator's default DNS lookup is used.
    /// </summary>
    public AlertDestinationGuard(Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _hostAddressResolver = hostAddressResolver;
    }

    /// <summary>
    /// Vets an alert delivery destination.
    /// </summary>
    /// <param name="destination">Destination URL to vet.</param>
    /// <param name="subject">
    /// Operator-facing name of the configured destination (for example "Slack webhook URL"), used
    /// as the prefix of the failure message.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
