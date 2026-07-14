// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Pure evaluation of embed key scope against a request. This is the
/// authoritative server-side decision: origin/domain (CORS), service, content,
/// tenant, and rate-limit checks. It has no I/O so it can be unit tested in
/// isolation and reused by every protocol surface.
/// </summary>
public static class EmbedPolicyEvaluator
{
    /// <summary>Default capabilities advertised to embeds.</summary>
    private static readonly string[] _defaultCapabilities = ["view", "search", "identify"];

    /// <summary>
    /// Evaluates whether an embed request is permitted by a key.
    /// </summary>
    /// <param name="key">The embed key, including its scope.</param>
    /// <param name="request">The request inputs.</param>
    /// <param name="now">The reference instant for lifecycle checks.</param>
    /// <returns>The policy decision.</returns>
    public static EmbedPolicyDecision Evaluate(EmbedKeyRecord key, EmbedPolicyRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);

        if (key.GetStatus(now) != EmbedKeyStatus.Active)
        {
            return EmbedPolicyDecision.Deny(EmbedPolicyDenyReason.KeyInactive, "embed key is not active");
        }

        var scope = key.Scope;

        if (!IsOriginAllowed(request.Origin, scope.AllowedEmbedOrigins))
        {
            return EmbedPolicyDecision.Deny(
                EmbedPolicyDenyReason.OriginNotAllowed,
                $"origin '{request.Origin ?? "(none)"}' is not allowed for this key");
        }

        if (!IsMemberAllowed(request.ServiceId, scope.AllowedServiceOrigins))
        {
            return EmbedPolicyDecision.Deny(
                EmbedPolicyDenyReason.ServiceNotAllowed,
                $"service '{request.ServiceId}' is not allowed for this key");
        }

        if (!IsMemberAllowed(request.ContentId, scope.AllowedContentIds))
        {
            return EmbedPolicyDecision.Deny(
                EmbedPolicyDenyReason.ContentNotAllowed,
                $"content '{request.ContentId}' is not allowed for this key");
        }

        if (!string.IsNullOrWhiteSpace(scope.TenantId)
            && !string.IsNullOrWhiteSpace(request.TenantId)
            && !string.Equals(scope.TenantId, request.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return EmbedPolicyDecision.Deny(
                EmbedPolicyDenyReason.TenantMismatch,
                "request tenant does not match the key's bound tenant");
        }

        if (scope.RateLimitRequestsPerWindow > 0
            && request.RequestsConsumedInWindow > scope.RateLimitRequestsPerWindow)
        {
            return EmbedPolicyDecision.Deny(
                EmbedPolicyDenyReason.RateLimited,
                "embed key has exceeded its rate budget for the current window");
        }

        return EmbedPolicyDecision.Allow;
    }

    /// <summary>
    /// Projects a key scope into the policy payload consumed by the embed
    /// governance adapter.
    /// </summary>
    /// <param name="key">The embed key.</param>
    /// <returns>The policy payload.</returns>
    public static EmbedPolicy BuildPolicy(EmbedKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var scope = key.Scope;

        return new EmbedPolicy
        {
            IntegrationId = scope.IntegrationId,
            TenantId = scope.TenantId,
            Edition = scope.Edition,
            AllowedOrigins = scope.AllowedEmbedOrigins,
            AllowedServices = scope.AllowedServiceOrigins,
            AllowedContentIds = scope.AllowedContentIds,
            Capabilities = _defaultCapabilities,
            RateLimit = new EmbedRateLimitPolicy
            {
                RequestsPerWindow = scope.RateLimitRequestsPerWindow,
                WindowSeconds = (int)Math.Max(1, scope.RateLimitWindow.TotalSeconds),
            },
        };
    }

    /// <summary>
    /// Normalizes a browser origin to a comparable lowercase scheme+host[:port]
    /// form, stripping any path/trailing slash.
    /// </summary>
    /// <param name="origin">The raw origin value.</param>
    /// <returns>The normalized origin, or <c>null</c> when blank.</returns>
    public static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var trimmed = origin.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var authority = uri.IsDefaultPort
                ? uri.Host
                : $"{uri.Host}:{uri.Port}";
            return $"{uri.Scheme}://{authority}".ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool IsOriginAllowed(string? origin, IReadOnlyList<string> allowed)
    {
        // Origins are security critical: an empty allow-list denies everything,
        // and a request without an Origin header cannot satisfy a restricted key.
        if (allowed.Count == 0)
        {
            return false;
        }

        var normalized = NormalizeOrigin(origin);
        if (normalized is null)
        {
            return allowed.Any(static entry => entry.Trim() == "*");
        }

        var host = HostOf(normalized);

        foreach (var candidate in allowed.Select(static entry => entry.Trim()))
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            if (candidate == "*")
            {
                return true;
            }

            if (candidate.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = candidate[1..].ToLowerInvariant(); // ".example.com"
                if (host is not null && host.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            var candidateNormalized = NormalizeOrigin(candidate);
            if (candidateNormalized is not null
                && string.Equals(candidateNormalized, normalized, StringComparison.Ordinal))
            {
                return true;
            }

            // Allow a bare host entry (no scheme) to match the request host.
            if (host is not null && string.Equals(candidate.ToLowerInvariant(), host, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMemberAllowed(string? value, IReadOnlyList<string> allowed)
    {
        // Service/content lists narrow when populated, allow-all when empty.
        if (allowed.Count == 0)
        {
            return true;
        }

        if (allowed.Any(static entry => entry.Trim() == "*"))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            // A restricted list cannot be satisfied by an unspecified value.
            return false;
        }

        return allowed.Any(entry => string.Equals(entry.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? HostOf(string normalizedOrigin)
    {
        var schemeIndex = normalizedOrigin.IndexOf("://", StringComparison.Ordinal);
        var afterScheme = schemeIndex >= 0 ? normalizedOrigin[(schemeIndex + 3)..] : normalizedOrigin;
        var portIndex = afterScheme.IndexOf(':', StringComparison.Ordinal);
        return portIndex >= 0 ? afterScheme[..portIndex] : afterScheme;
    }
}
