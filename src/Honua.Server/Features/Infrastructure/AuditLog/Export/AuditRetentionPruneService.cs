// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Background job that enforces the configurable audit retention policy (#509):
/// periodically removes audit records older than <c>AuditLog:Export:RetentionDays</c>
/// from the audit store via the registered <see cref="IAuditRetentionPruner"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only registered when a bounded retention window is configured
/// (<c>RetentionDays &gt; 0</c>); an unbounded policy means "retain forever" and
/// no sweep is scheduled. The sweep runs once shortly after startup and then on
/// a fixed interval. Failures are logged and swallowed so a transient database
/// hiccup never crashes the host — the next sweep retries.
/// </para>
/// <para>
/// The pruner itself only ever deletes a contiguous head prefix of the
/// tamper-evident chain, so retention never breaks integrity verification.
/// </para>
/// </remarks>
internal sealed partial class AuditRetentionPruneService : BackgroundService
{
    /// <summary>How often the retention sweep runs.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    /// <summary>Grace delay before the first sweep so startup work settles first.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditRetentionPolicy _policy;
    private readonly ILogger<AuditRetentionPruneService> _logger;

    public AuditRetentionPruneService(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditExportOptions> options,
        ILogger<AuditRetentionPruneService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _policy = options.Value.ToRetentionPolicy();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defensive: the service is only registered when bounded, but guard anyway.
        if (!_policy.IsBounded)
        {
            return;
        }

        LogStarted(_logger, _policy.RetentionWindow);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(SweepInterval);
            do
            {
                await PruneOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }
    }

    private async Task PruneOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var pruner = scope.ServiceProvider.GetRequiredService<IAuditRetentionPruner>();
            var removed = await pruner.PruneAsync(_policy, stoppingToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogSweepRemoved(_logger, removed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a retention failure crash the host; the next tick retries.
            LogSweepFailed(_logger, ex);
        }
    }

    [LoggerMessage(
        EventId = 7320,
        Level = LogLevel.Information,
        Message = "Audit retention sweep enabled (window {RetentionWindow}).")]
    private static partial void LogStarted(ILogger logger, TimeSpan retentionWindow);

    [LoggerMessage(
        EventId = 7321,
        Level = LogLevel.Information,
        Message = "Audit retention sweep removed {RemovedCount} expired audit record(s).")]
    private static partial void LogSweepRemoved(ILogger logger, int removedCount);

    [LoggerMessage(
        EventId = 7322,
        Level = LogLevel.Warning,
        Message = "Audit retention sweep failed; will retry on the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
