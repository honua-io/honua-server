// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// A signed logout request is honoured while fresh, only when addressed to this SP, and only once its session has ended.
internal sealed class SamlLogoutRequestValidator(
    IOptions<SamlAuthenticationOptions> options,
    SamlAssertionReplayCache replayCache,
    TimeProvider? timeProvider = null)
{
    private const string ReplayKeyPrefix = "logout:";
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

    // Records the request as used once its session has ended, so a retry after a failed termination still works.
    public bool TryConsume(XElement logoutRequest)
    {
        ArgumentNullException.ThrowIfNull(logoutRequest);
        var id = logoutRequest.Attribute("ID")?.Value?.Trim();
        var issuedAt = ParseInstant(logoutRequest.Attribute("IssueInstant")?.Value);
        return !string.IsNullOrEmpty(id) && issuedAt is { } issued &&
            replayCache.TryRemember(ReplayKeyPrefix + id, issued + Skew + _maximumAge);
    }

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
