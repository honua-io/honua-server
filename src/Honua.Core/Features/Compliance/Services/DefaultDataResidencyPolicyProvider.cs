// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Configuration-bound data residency provider. The active policy is read from
/// <see cref="ComplianceOptions.DataResidency"/>; the primary region is always
/// considered allowed even if not explicitly listed (this matches operator
/// expectation — "I live in <c>us-gov-west-1</c>" never gets blocked).
/// </summary>
internal sealed class DefaultDataResidencyPolicyProvider : IDataResidencyPolicyProvider
{
    private readonly IOptionsMonitor<ComplianceOptions> _options;

    public DefaultDataResidencyPolicyProvider(IOptionsMonitor<ComplianceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public DataResidencyPolicy GetPolicy()
    {
        var opts = _options.CurrentValue;
        var residency = opts.DataResidency;

        var primary = string.IsNullOrWhiteSpace(residency.PrimaryRegion)
            ? opts.PrimaryRegion
            : residency.PrimaryRegion;

        if (string.IsNullOrWhiteSpace(primary))
        {
            primary = "unspecified";
        }

        var allowed = NormalizeAllowedRegions(residency.AllowedRegions, primary);

        return new DataResidencyPolicy
        {
            Enforced = residency.Enforced,
            PrimaryRegion = primary,
            AllowedRegions = allowed,
        };
    }

    public DataResidencyDecision Evaluate(string region)
    {
        var policy = GetPolicy();

        if (string.IsNullOrWhiteSpace(region))
        {
            return DataResidencyDecision.Deny(region ?? string.Empty, policy,
                "Region is empty — egress requires an explicit region.");
        }

        if (!policy.Enforced)
        {
            return DataResidencyDecision.Allow(region, policy,
                "Residency policy is informational only — egress allowed but recorded.");
        }

        if (policy.IsRegionAllowed(region))
        {
            return DataResidencyDecision.Allow(region, policy,
                $"Region '{region}' is listed in the active residency allow-list.");
        }

        return DataResidencyDecision.Deny(region, policy,
            $"Region '{region}' is not in the active residency allow-list (primary: {policy.PrimaryRegion}).");
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> NormalizeAllowedRegions(
        List<string> configured,
        string primary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(configured.Count + 1);

        if (!string.IsNullOrWhiteSpace(primary) && seen.Add(primary))
        {
            ordered.Add(primary);
        }

        for (var i = 0; i < configured.Count; i++)
        {
            var region = configured[i];
            if (string.IsNullOrWhiteSpace(region))
            {
                continue;
            }

            if (seen.Add(region.Trim()))
            {
                ordered.Add(region.Trim());
            }
        }

        return ordered.AsReadOnly();
    }
}
