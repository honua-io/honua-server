// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Settings for the scheduled audit hash-chain verification loop
/// (<c>Observability:AuditChainVerification</c>, #2810). A dormant background service replays the
/// tamper-evident <c>audit_log</c> chain on a cadence and publishes the result so the
/// <c>audit-chain-integrity</c> ops finding fires on the first broken link — without waiting for an
/// operator to hit the pull-only <c>/verify</c> endpoint.
/// </summary>
public sealed class AuditChainVerificationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Observability:AuditChainVerification";

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled verification loop runs. Default is true.
    /// When disabled the audit chain is only verified on demand via the <c>/verify</c> endpoint.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the interval between full chain-verification passes. A full replay scans the
    /// audit trail, so the default is deliberately infrequent (1 hour); tune up for very large trails.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the delay before the first verification pass after startup, so boot does not
    /// contend with migrations and warm-up. Default is 5 minutes.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
}
