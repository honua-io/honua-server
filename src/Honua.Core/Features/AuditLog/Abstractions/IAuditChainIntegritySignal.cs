// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Live snapshot of the most recent scheduled audit hash-chain verification (#2810). The audit trail
/// is append-only and hash-chained, but <c>/verify</c> was pull-only: nothing scheduled a check and
/// nothing raised a signal on a broken chain, so tail-tampering by a superuser who can drop the
/// append-only rules stayed invisible until someone manually verified. A background verifier keeps
/// this snapshot current; readiness/health surfaces and the deterministic ops-findings engine read
/// it to fire a paged signal / finding on the first broken link.
/// </summary>
/// <remarks>
/// This is a read-only signal seam: the verifier writes it, consumers only read it. It carries no
/// database access, so a health check or finding rule can consult it without a per-probe query.
/// </remarks>
public interface IAuditChainIntegritySignal
{
    /// <summary>
    /// True once at least one scheduled verification pass has completed (so <see cref="IsChainBroken"/>
    /// and the report reflect a real observation rather than the pre-first-pass default).
    /// </summary>
    bool HasVerified { get; }

    /// <summary>Timestamp of the most recent completed verification pass, or <c>null</c> before the first.</summary>
    DateTimeOffset? LastVerifiedAt { get; }

    /// <summary>
    /// True when the most recent completed verification found a broken link in the hash chain. Rows
    /// written before hashing was introduced are reported as unhashed, not broken, so they never set
    /// this flag.
    /// </summary>
    bool IsChainBroken { get; }

    /// <summary>The most recent completed verification report, or <c>null</c> before the first pass.</summary>
    AuditIntegrityReport? LastReport { get; }
}
