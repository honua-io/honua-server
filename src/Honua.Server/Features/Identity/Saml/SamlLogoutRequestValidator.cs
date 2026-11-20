// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// A signed logout request is honoured while fresh, only when addressed to this SP, and only once: it is claimed
// for the caller before the session ends, so a repeat from anyone else is refused without ending a session.
internal sealed class SamlLogoutRequestValidator(
    IOptions<SamlAuthenticationOptions> options,
    SamlAssertionReplayCache replayCache,
    TimeProvider? timeProvider = null)
{
    private const string ReplayKeyPrefix = "logout:";
    private const string PendingMarkerPrefix = "saml-logout-pending:";
    private const string CallerDigestPurpose = "honua-saml-logout-owner:";
    private static readonly TimeSpan _maximumAge = TimeSpan.FromMinutes(5);

    private readonly SamlAuthenticationOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Returns the rejection reason, or null when the request may end the session.
    public string? Validate(XElement logoutRequest)
    {
        ArgumentNullException.ThrowIfNull(logoutRequest);

        var id = logoutRequest.Attribute("ID")?.Value?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return "SAML logout request has no ID.";
        }

        if (ParseInstant(logoutRequest.Attribute("IssueInstant")?.Value) is not { } issuedAt)
        {
            return "SAML logout request has no valid IssueInstant.";
        }

        return TimeWindowFailure(logoutRequest, issuedAt, Skew) ?? DestinationFailure(logoutRequest);
    }

    // Claims the request for this caller before any session is ended. Only the same caller may claim it
    // again (a retry after a failed termination); any other repeat is refused before it can end a session.
    public Task<bool> TryClaimAsync(XElement logoutRequest, string? callerSessionId, CancellationToken cancellationToken)
        => Claim(logoutRequest, callerSessionId) is { } claim
            ? replayCache.TryClaimAsync(claim, cancellationToken)
            : Task.FromResult(false);

    // Marks the request used once the caller's session has ended; no later claim can match it.
    public Task<bool> TryCompleteAsync(XElement logoutRequest, string? callerSessionId, CancellationToken cancellationToken)
        => Claim(logoutRequest, callerSessionId) is { } claim
            ? replayCache.TryFinalizeAsync(claim, cancellationToken)
            : Task.FromResult(false);

    private SamlReplayClaim? Claim(XElement logoutRequest, string? callerSessionId)
    {
        ArgumentNullException.ThrowIfNull(logoutRequest);
        var id = logoutRequest.Attribute("ID")?.Value?.Trim();
        return !string.IsNullOrEmpty(id) && ParseInstant(logoutRequest.Attribute("IssueInstant")?.Value) is { } issued
            ? new SamlReplayClaim(ReplayKeyPrefix + id, CallerMarker(callerSessionId), issued + Skew + _maximumAge)
            : null;
    }

    // The store keeps only a purpose-bound digest of the caller's session cookie, never the cookie itself,
    // and the digest differs from the one that keys the session record.
    private static string CallerMarker(string? callerSessionId)
        => PendingMarkerPrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CallerDigestPurpose + (callerSessionId ?? string.Empty))));

    private TimeSpan Skew => TimeSpan.FromSeconds(Math.Max(0, _options.AllowedClockSkewSeconds));

    private string? TimeWindowFailure(XElement logoutRequest, DateTimeOffset issuedAt, TimeSpan skew)
    {
        var now = _time.GetUtcNow();
        if (issuedAt > now + skew || issuedAt < now - skew - _maximumAge)
        {
            return "SAML logout request is outside its validity window.";
        }

        return ParseInstant(logoutRequest.Attribute("NotOnOrAfter")?.Value) is { } notOnOrAfter && now - skew >= notOnOrAfter
            ? "SAML logout request has expired."
            : null;
    }

    private string? DestinationFailure(XElement logoutRequest)
    {
        if (string.IsNullOrWhiteSpace(_options.SingleLogoutServiceUrl))
        {
            return "Single logout is not configured for this SP.";
        }

        var destination = logoutRequest.Attribute("Destination")?.Value?.Trim();
        return string.Equals(destination, _options.SingleLogoutServiceUrl.Trim(), StringComparison.Ordinal)
            ? null
            : "SAML logout request Destination does not match the single logout service.";
    }

    private static DateTimeOffset? ParseInstant(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var instant)
            ? instant
            : null;
}
