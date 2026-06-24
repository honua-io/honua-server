// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Verifies the tamper-evident hash chain of the append-only audit trail (#350).
/// </summary>
/// <remarks>
/// Operators and SIEM tooling use this to prove the audit trail has not been
/// rewritten: the verifier replays the chain (recomputing each row's
/// <c>entry_hash</c> from its predecessor and immutable fields) and reports the
/// first row, if any, where the recomputed hash diverges from the stored hash.
/// </remarks>
public interface IAuditLogIntegrityVerifier
{
    /// <summary>
    /// Replays and verifies the audit hash chain.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an audit hash-chain verification pass.
/// </summary>
public sealed record AuditIntegrityReport
{
    /// <summary>
    /// <c>true</c> when every hashed row's recomputed <c>entry_hash</c> matched
    /// the stored value and each row's <c>prev_hash</c> linked correctly to its
    /// predecessor.
    /// </summary>
    public required bool Verified { get; init; }

    /// <summary>Total number of rows examined.</summary>
    public required long RowsChecked { get; init; }

    /// <summary>
    /// Number of leading rows with no hash (written before migration 069). These
    /// are reported but not treated as a tamper failure.
    /// </summary>
    public required long UnhashedRows { get; init; }

    /// <summary>
    /// The <c>audit_id</c> of the first row whose hash did not verify, or
    /// <c>null</c> when the chain is intact.
    /// </summary>
    public long? FirstBrokenAuditId { get; init; }

    /// <summary>
    /// Human-readable description of the first integrity failure, or <c>null</c>
    /// when the chain is intact.
    /// </summary>
    public string? FailureReason { get; init; }
}
