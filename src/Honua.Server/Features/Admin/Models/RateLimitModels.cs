// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for a rate limit policy.
/// </summary>
public sealed class RateLimitPolicyResponse
{
    /// <summary>
    /// Policy identifier.
    /// </summary>
    public required Guid PolicyId { get; init; }

    /// <summary>
    /// Policy name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Policy scope (tenant, api-key, endpoint).
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    /// The key this policy is bound to.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Maximum requests per window.
    /// </summary>
    public required int RequestsPerWindow { get; init; }

    /// <summary>
    /// Window duration in seconds.
    /// </summary>
    public required double WindowDurationSeconds { get; init; }

    /// <summary>
    /// Whether the policy is active.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When the policy was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the policy was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Request to create a rate limit policy.
/// </summary>
public sealed class CreateRateLimitPolicyRequest
{
    /// <summary>
    /// Policy name.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Policy scope.
    /// </summary>
    [Required]
    public required string Scope { get; init; }

    /// <summary>
    /// The key to bind to.
    /// </summary>
    [Required]
    public required string Key { get; init; }

    /// <summary>
    /// Maximum requests per window.
    /// </summary>
    [Range(1, 1_000_000)]
    public required int RequestsPerWindow { get; init; }

    /// <summary>
    /// Window duration in seconds.
    /// </summary>
    [Range(1, 86400)]
    public required double WindowDurationSeconds { get; init; }

    /// <summary>
    /// Whether the policy is active.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Request to update a rate limit policy.
/// </summary>
public sealed class UpdateRateLimitPolicyRequest
{
    /// <summary>
    /// Updated policy name.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>
    /// Updated requests per window.
    /// </summary>
    [Range(1, 1_000_000)]
    public int? RequestsPerWindow { get; init; }

    /// <summary>
    /// Updated window duration in seconds.
    /// </summary>
    [Range(1, 86400)]
    public double? WindowDurationSeconds { get; init; }

    /// <summary>
    /// Updated enabled state.
    /// </summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// API response model for current rate limit status.
/// </summary>
public sealed class RateLimitStatusResponse
{
    /// <summary>
    /// Policy identifier.
    /// </summary>
    public required Guid PolicyId { get; init; }

    /// <summary>
    /// The rate-limited key.
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
