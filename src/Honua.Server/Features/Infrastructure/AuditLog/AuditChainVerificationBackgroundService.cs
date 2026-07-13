// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.AuditLog;

/// <summary>
/// Scheduled verifier for the tamper-evident audit hash chain (#2810). The audit trail is
/// append-only and hash-chained, but verification was pull-only (<c>/verify</c>): nothing scheduled a
/// check and nothing raised a signal on a broken chain, so tail-tampering by a superuser who can drop
/// the append-only rules stayed invisible until someone manually verified. This service replays the
/// chain on a fixed interval, publishes the outcome through <see cref="IAuditChainIntegritySignal"/>,
/// and on the first broken link logs a critical event and drives the
/// <c>honua.audit.chain_broken</c> gauge to 1 — so the broken chain raises a paged signal
/// (health-check fault / ops finding) instead of waiting for a manual check.
/// </summary>
/// <remarks>
/// Failures of the verification pass itself (e.g. a transient database error) are logged and
/// swallowed so a hiccup never crashes the host; the previous good/broken signal is retained and the
/// next tick retries. A full replay is O(rows), hence the deliberately infrequent default interval.
/// </remarks>
internal sealed partial class AuditChainVerificationBackgroundService : BackgroundService, IAuditChainIntegritySignal
{
    private static long _chainBrokenGauge;

    static AuditChainVerificationBackgroundService()
    {
        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.audit.chain_broken",
            static () => Interlocked.Read(ref _chainBrokenGauge),
            unit: "{state}",
            description: "1 when the most recent scheduled audit hash-chain verification found a broken link; else 0.");
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditChainVerificationOptions _options;
    private readonly ILogger<AuditChainVerificationBackgroundService> _logger;

    private volatile AuditIntegrityReport? _lastReport;
    private long _lastVerifiedAtTicks;

    public AuditChainVerificationBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditChainVerificationOptions> options,
        ILogger<AuditChainVerificationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool HasVerified => Interlocked.Read(ref _lastVerifiedAtTicks) != 0;

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
    public bool IsChainBroken => _lastReport is { Verified: false };

    /// <inheritdoc />
    public AuditIntegrityReport? LastReport => _lastReport;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        LogStarted(_logger, _options.Interval);

        try
        {
            await Task.Delay(_options.InitialDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_options.Interval);
            do
            {
                await VerifyOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }
    }

    internal async Task VerifyOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var verifier = scope.ServiceProvider.GetService<IAuditLogIntegrityVerifier>();
            if (verifier is null)
            {
                // No database-backed verifier in this host (e.g. non-Postgres / test compositions);
                // nothing to verify.
                return;
            }

            var report = await verifier.VerifyAsync(stoppingToken).ConfigureAwait(false);
            Publish(report);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a verification failure crash the host; retain the prior signal and retry.
            LogVerificationFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Publishes a completed verification report to the signal and, when the chain is broken, logs a
    /// critical event and raises the gauge. Exposed for direct-unit coverage of the broken-chain path.
    /// </summary>
    internal void Publish(AuditIntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _lastReport = report;
        Interlocked.Exchange(ref _lastVerifiedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        if (report.Verified)
        {
            Interlocked.Exchange(ref _chainBrokenGauge, 0);
            LogVerified(_logger, report.RowsChecked, report.UnhashedRows);
            return;
        }

        Interlocked.Exchange(ref _chainBrokenGauge, 1);
        LogChainBroken(
            _logger,
            report.FirstBrokenAuditId ?? -1,
            report.RowsChecked,
            report.FailureReason ?? "unknown");
    }

    [LoggerMessage(EventId = 7330, Level = LogLevel.Information, Message = "Scheduled audit hash-chain verification is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 7331, Level = LogLevel.Information, Message = "Scheduled audit hash-chain verification enabled (interval {Interval}).")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 7332, Level = LogLevel.Debug, Message = "Audit hash-chain verified: {RowsChecked} row(s) checked, {UnhashedRows} unhashed.")]
    private static partial void LogVerified(ILogger logger, long rowsChecked, long unhashedRows);

    [LoggerMessage(
        EventId = 7333,
        Level = LogLevel.Critical,
        Message = "Audit hash-chain integrity FAILED: first broken link at audit_id {FirstBrokenAuditId} after {RowsChecked} row(s). Reason: {FailureReason}. The append-only audit trail may have been tampered with.")]
    private static partial void LogChainBroken(ILogger logger, long firstBrokenAuditId, long rowsChecked, string failureReason);

    [LoggerMessage(EventId = 7334, Level = LogLevel.Warning, Message = "Scheduled audit hash-chain verification pass failed; retaining the previous result and retrying on the next interval.")]
    private static partial void LogVerificationFailed(ILogger logger, Exception exception);
}
