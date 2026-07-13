// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Scheduled verifier for the tamper-evident <c>audit_log</c> hash chain (#2810). The chain was
/// previously only checked on demand via the pull-only <c>/verify</c> endpoint, so tail-tampering by
/// a superuser who can <c>DROP RULE</c> stayed invisible until someone remembered to look. This loop
/// replays the chain on a cadence and publishes the result to <see cref="IAuditChainVerificationSignal"/>,
/// which the <c>audit-chain-integrity</c> ops finding surfaces as a critical finding on the first broken link.
/// </summary>
/// <remarks>
/// The verifier is Postgres-only and resolved optionally per pass, so the loop is inert on providers
/// without an integrity verifier. When a durable job-store lease is available it is taken so only one
/// node in a cluster replays the (whole-chain) scan; without it every node verifies independently,
/// which is redundant but safe for a low-frequency tamper check.
/// </remarks>
internal sealed partial class AuditChainVerificationBackgroundService : BackgroundService
{
    private const string LeaseOperationId = "audit-chain-verification";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditChainVerificationSignal _signal;
    private readonly IOptionsMonitor<AuditChainVerificationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditChainVerificationBackgroundService> _logger;
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public AuditChainVerificationBackgroundService(
        IServiceScopeFactory scopeFactory,
        IAuditChainVerificationSignal signal,
        IOptionsMonitor<AuditChainVerificationOptions> options,
        TimeProvider timeProvider,
        ILogger<AuditChainVerificationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        try
        {
            await Task.Delay(_options.CurrentValue.InitialDelay, _timeProvider, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_options.CurrentValue.Enabled)
                {
                    await VerifyOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(ResolveInterval(_options.CurrentValue), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one verification pass: resolves the integrity verifier (if any), takes the durable lease
    /// (if available), replays the chain, and publishes the result. Safe to call directly in tests.
    /// </summary>
    internal async Task VerifyOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var verifier = scope.ServiceProvider.GetService<IAuditLogIntegrityVerifier>();
        if (verifier is null)
        {
            // No integrity verifier for the active provider; nothing to check.
            return;
        }

        var jobStore = scope.ServiceProvider.GetService<IExecutionJobStore>();
        var leased = jobStore is not null
            && await jobStore.TryAcquireLeaseAsync(LeaseOperationId, _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (jobStore is not null && !leased)
        {
            // Another node holds the verification lease this pass.
            return;
        }

        try
        {
            var report = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
            _signal.Publish(report, _timeProvider.GetUtcNow());

            if (report.Verified)
            {
                LogVerified(_logger, report.RowsChecked, report.UnhashedRows);
            }
            else
            {
                LogChainBroken(_logger, report.FirstBrokenAuditId ?? -1, report.FailureReason ?? "unspecified");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A verification failure (e.g. transient DB error) must not crash the host loop; the
            // previous published result stands and the next pass retries.
            LogVerificationFailed(_logger, ex);
        }
        finally
        {
            if (leased && jobStore is not null)
            {
                await jobStore.ReleaseLeaseAsync(LeaseOperationId, _ownerId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ResolveInterval(AuditChainVerificationOptions options)
        => options.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.Interval;

    [LoggerMessage(EventId = 9440, Level = LogLevel.Information, Message = "Scheduled audit-chain verification is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 9441, Level = LogLevel.Debug, Message = "Audit-chain verification passed over {RowsChecked} rows ({UnhashedRows} legacy unhashed).")]
    private static partial void LogVerified(ILogger logger, long rowsChecked, long unhashedRows);

    [LoggerMessage(EventId = 9442, Level = LogLevel.Critical, Message = "Audit-chain verification failed: the tamper-evident hash chain is broken at audit_id {FirstBrokenAuditId} ({FailureReason}).")]
    private static partial void LogChainBroken(ILogger logger, long firstBrokenAuditId, string failureReason);

    [LoggerMessage(EventId = 9443, Level = LogLevel.Warning, Message = "Audit-chain verification pass errored; the previous result stands and the next pass will retry.")]
    private static partial void LogVerificationFailed(ILogger logger, Exception exception);
}
