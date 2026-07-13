// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.AuditLog;

/// <summary>
/// Configuration for the scheduled audit hash-chain verifier (#2810). The verifier periodically
/// replays the append-only audit chain and publishes the result as a signal so a broken link raises
/// a paged health fault / ops finding instead of being caught only on a manual <c>/verify</c> call.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AuditChainVerificationOptions
{
    /// <summary>Configuration section used to bind these options.</summary>
    public const string SectionName = "AuditLog:ChainVerification";

    /// <summary>
    /// Enables the scheduled verifier. On by default: a self-operating platform must catch
    /// audit-chain tampering without waiting for a manual verification. Set to <c>false</c> to
    /// disable scheduling (the pull-based <c>/verify</c> endpoint remains available regardless).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Interval between full chain-verification passes. A full replay is O(rows), so the default is
    /// deliberately infrequent (every 6 hours) to bound cost on large audit tables; the manual
    /// endpoint covers on-demand checks.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Grace delay before the first pass so startup work (migrations, warm-up) settles first.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(2);
}
