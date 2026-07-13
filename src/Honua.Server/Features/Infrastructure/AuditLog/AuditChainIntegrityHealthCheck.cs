// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Honua.Server.Features.Infrastructure.AuditLog;

/// <summary>
/// Surfaces the scheduled audit hash-chain verification result (#2810) through the
/// <see cref="HealthCheckService"/> roll-up (and the ops-health snapshot's <c>overallStatus</c>) so a
/// broken/tampered chain is a paged signal rather than something caught only on a manual
/// <c>/verify</c>. Reports <b>Unhealthy</b> once a completed verification found a broken link;
/// otherwise Healthy (including before the first pass, when there is nothing yet to report). It reads
/// the cached <see cref="IAuditChainIntegritySignal"/> snapshot, so it never runs a chain replay on a
/// probe.
/// </summary>
/// <remarks>
/// This is intentionally on the health-check roll-up rather than the depooling readiness probe:
/// audit tampering must page loudly, but it does not make the node unable to serve traffic, so
/// depooling would be the wrong response.
/// </remarks>
internal sealed class AuditChainIntegrityHealthCheck : IHealthCheck
{
    private readonly IAuditChainIntegritySignal _signal;

    public AuditChainIntegrityHealthCheck(IAuditChainIntegritySignal signal)
        => _signal = signal ?? throw new ArgumentNullException(nameof(signal));

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_signal.HasVerified)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Audit hash-chain has not yet been verified this run."));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        if (_signal.LastVerifiedAt is { } verifiedAt)
        {
            data["last_verified_at"] = verifiedAt.ToString("o", CultureInfo.InvariantCulture);
        }

        var report = _signal.LastReport;
        if (report is not null)
        {
            data["rows_checked"] = report.RowsChecked;
            data["unhashed_rows"] = report.UnhashedRows;
        }

        if (_signal.IsChainBroken)
        {
            if (report?.FirstBrokenAuditId is { } brokenId)
            {
                data["first_broken_audit_id"] = brokenId;
            }

            return Task.FromResult(HealthCheckResult.Unhealthy(
                report?.FailureReason is { Length: > 0 } reason
                    ? $"Audit hash-chain integrity failed: {reason}. The append-only audit trail may have been tampered with."
                    : "Audit hash-chain integrity failed; the append-only audit trail may have been tampered with.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Audit hash-chain verified intact.", data));
    }
}
