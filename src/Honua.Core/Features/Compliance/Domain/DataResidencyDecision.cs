// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Decision returned by a residency policy evaluation. Mirrors the shape of
/// <see cref="Security.Domain.AccessDecision"/> so call-sites compose cleanly.
/// </summary>
public sealed record DataResidencyDecision
{
    /// <summary>Result of allowed evaluation.</summary>
    public required bool Allowed { get; init; }

    /// <summary>The region the policy was evaluated against.</summary>
    public required string Region { get; init; }

    /// <summary>The active policy that produced the decision.</summary>
    public required DataResidencyPolicy Policy { get; init; }

    /// <summary>
    /// Human-readable reason for the decision. Always populated even on allow,
    /// so audit log entries carry the justification verbatim.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>Create an allow decision.</summary>
    public static DataResidencyDecision Allow(string region, DataResidencyPolicy policy, string reason) =>
        new() { Allowed = true, Region = region, Policy = policy, Reason = reason };

    /// <summary>Create a deny decision.</summary>
    public static DataResidencyDecision Deny(string region, DataResidencyPolicy policy, string reason) =>
        new() { Allowed = false, Region = region, Policy = policy, Reason = reason };
}
