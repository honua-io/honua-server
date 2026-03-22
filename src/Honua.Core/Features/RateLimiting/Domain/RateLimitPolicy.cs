// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

/// <summary>
/// Defines a rate limit policy for a tenant, API key, or endpoint.
/// </summary>
public sealed class RateLimitPolicy
{
    /// <summary>
    /// Unique identifier for this policy.
    /// </summary>
    public Guid PolicyId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable policy name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The scope this policy applies to (e.g., "tenant", "api-key", "endpoint").
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    /// The key this policy is bound to (e.g., tenant ID, API key prefix, endpoint pattern).
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Maximum number of requests allowed in the window.
    /// </summary>
    public required int RequestsPerWindow { get; init; }

    /// <summary>
    /// Duration of the rate limit window.
    /// </summary>
    public required TimeSpan WindowDuration { get; init; }

    /// <summary>
    /// Whether this policy is currently active.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When the policy was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the policy was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Current rate limit status for a specific key/tenant.
/// </summary>
public sealed class RateLimitStatus
{
    /// <summary>
    /// The policy this status applies to.
    /// </summary>
    public required Guid PolicyId { get; init; }

    /// <summary>
    /// The key being rate-limited.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Requests consumed in the current window.
    /// </summary>
    public required int RequestsConsumed { get; init; }

    /// <summary>
    /// Requests remaining in the current window.
    /// </summary>
    public required int RequestsRemaining { get; init; }

    /// <summary>
    /// When the current window resets.
    /// </summary>
    public required DateTimeOffset WindowResetsAt { get; init; }
}
