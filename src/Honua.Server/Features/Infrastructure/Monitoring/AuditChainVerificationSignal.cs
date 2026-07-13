// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Holds the most recent scheduled audit hash-chain verification result so the
/// <c>audit-chain-integrity</c> ops finding can read it without re-scanning the chain on every
/// evaluation (#2810). Written by <see cref="AuditChainVerificationBackgroundService"/>.
/// </summary>
internal interface IAuditChainVerificationSignal
{
    /// <summary>
    /// The most recent verification report, or null before the first scheduled pass has completed.
    /// </summary>
    AuditIntegrityReport? LastReport { get; }

    /// <summary>Timestamp of the most recent completed verification pass, or null before the first pass.</summary>
    DateTimeOffset? LastVerifiedAt { get; }

    /// <summary>Records the outcome of a completed verification pass.</summary>
    /// <param name="report">The verification report.</param>
    /// <param name="verifiedAt">When the pass completed.</param>
    void Publish(AuditIntegrityReport report, DateTimeOffset verifiedAt);
}

/// <summary>
/// Default in-memory <see cref="IAuditChainVerificationSignal"/>. Registered as a singleton so the
/// background writer and the (scoped) findings reader share one snapshot.
/// </summary>
internal sealed class AuditChainVerificationSignal : IAuditChainVerificationSignal
{
    private volatile AuditIntegrityReport? _lastReport;
    private long _lastVerifiedAtTicks;

    /// <inheritdoc />
    public AuditIntegrityReport? LastReport => _lastReport;

    /// <inheritdoc />
    public DateTimeOffset? LastVerifiedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastVerifiedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public void Publish(AuditIntegrityReport report, DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        _lastReport = report;
        Interlocked.Exchange(ref _lastVerifiedAtTicks, verifiedAt.UtcDateTime.Ticks);
    }
}
