// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Walks the control catalog and produces a <see cref="ComplianceSnapshot"/> by
/// inspecting server state (configuration, audit log, dependency gate, encryption
/// posture). The collector is the single integration point — endpoints, exports,
/// and the Admin UI dashboard all read the snapshot it produces.
/// </summary>
public interface IComplianceEvidenceCollector
{
    /// <summary>
    /// Collect a fresh snapshot. The collector resolves dependencies on each call —
    /// caching is the responsibility of callers (the snapshot is small and snapshot
    /// freshness matters more than CPU cost for audit workflows).
    /// </summary>
    Task<ComplianceSnapshot> CollectAsync(CancellationToken cancellationToken = default);
}
