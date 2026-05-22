// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Reads the active data-residency policy and evaluates whether a region is permitted.
/// </summary>
/// <remarks>
/// Wired into egress paths (cloud storage, external geocoder, federated query) so
/// data leaving the boundary is gated by a single policy source. Snapshot reads
/// must be cheap — the policy is consulted on every potential egress.
/// </remarks>
public interface IDataResidencyPolicyProvider
{
    /// <summary>The active policy. Never <c>null</c>; returns <see cref="DataResidencyPolicy.Disabled"/> when no operator-supplied policy exists.</summary>
    DataResidencyPolicy GetPolicy();

    /// <summary>Evaluate whether a region is allowed under the active policy.</summary>
    DataResidencyDecision Evaluate(string region);
}
