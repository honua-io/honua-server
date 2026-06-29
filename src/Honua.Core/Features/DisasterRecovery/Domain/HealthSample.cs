// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// A single automated health-check observation of the primary serving surface. Failover
/// playbooks consume an ordered series of these to decide whether the primary has failed
/// enough consecutive checks to warrant failover (#356, ADR-0024).
/// </summary>
/// <param name="ObservedAt">When the health check was taken.</param>
/// <param name="Healthy">Whether the primary responded healthily to this check.</param>
/// <param name="Reason">Operator-facing detail for an unhealthy check; null when healthy or unknown.</param>
public sealed record HealthSample(
    DateTimeOffset ObservedAt,
    bool Healthy,
    string? Reason = null);
