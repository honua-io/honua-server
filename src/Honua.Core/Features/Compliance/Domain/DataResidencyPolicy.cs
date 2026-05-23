// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Active data-residency policy for the deployment. Enforced at boundaries that
/// move data outside the server (cloud storage, external geocoders, federation),
/// not at every API call.
/// </summary>
/// <remarks>
/// <para>
/// Region codes are upper-case ISO-3166-1 alpha-2 country codes (<c>US</c>, <c>DE</c>)
/// or vendor regions (<c>us-gov-west-1</c>, <c>europe-west3</c>). The policy is a
/// closed set: anything not listed in <see cref="AllowedRegions"/> is rejected.
/// </para>
/// <para>
/// An empty <see cref="AllowedRegions"/> with <see cref="Enforced"/> = <c>true</c>
/// means "deny all egress" — useful for air-gapped FedRAMP High deployments. An
/// empty allowed set with <see cref="Enforced"/> = <c>false</c> means the policy
/// is informational only (audit-only mode, used during onboarding).
/// </para>
/// </remarks>
public sealed record DataResidencyPolicy
{
    /// <summary>Canonical "deny all egress" policy used when the operator has not configured residency.</summary>
    public static readonly DataResidencyPolicy Disabled = new()
    {
        Enforced = false,
        PrimaryRegion = "unspecified",
        AllowedRegions = Array.Empty<string>(),
    };

    /// <summary>
    /// Whether residency is enforced. When <c>false</c>, the residency check still
    /// records the attempted egress to the audit log but does not block it.
    /// </summary>
    public required bool Enforced { get; init; }

    /// <summary>
    /// Primary region for stored data — the region the deployment claims to operate in.
    /// Used for the FedRAMP system boundary diagram.
    /// </summary>
    public required string PrimaryRegion { get; init; }

    /// <summary>
    /// Set of regions data may flow to (including the primary). Region codes are
    /// stored as supplied (operator chooses ISO codes or vendor codes) and compared
    /// case-insensitively at enforcement time.
    /// </summary>
    public required IReadOnlyList<string> AllowedRegions { get; init; }

    /// <summary>
    /// Returns <c>true</c> if data may flow to the supplied region under this policy.
    /// </summary>
    public bool IsRegionAllowed(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return false;
        }

        for (var i = 0; i < AllowedRegions.Count; i++)
        {
            if (string.Equals(AllowedRegions[i], region, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
