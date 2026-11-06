// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

internal sealed class McpJobProgressBridge
{
    private const double ProgressTotal = 100d;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(750);

    // The cap stops a job that never reaches a terminal status from leaking a background poll for the process lifetime.
    private static readonly TimeSpan MaxTrackingDuration = TimeSpan.FromMinutes(30);

    private readonly IMcpNotificationPublisher _publisher;
    private readonly McpSessionManager _sessions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpJobProgressBridge> _logger;
    private readonly IOptions<McpOptions>? _options;
    private int _startedPolls;

    public McpJobProgressBridge(
        IMcpNotificationPublisher publisher,
        McpSessionManager sessions,
        IServiceScopeFactory scopeFactory,
        ILogger<McpJobProgressBridge> logger,
        IOptions<McpOptions>? options = null)
    {
        _publisher = publisher;
        _sessions = sessions;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    internal int StartedPollCount => Volatile.Read(ref _startedPolls);

    // Poll failures are logged and swallowed so they cannot surface on an unrelated request thread.
    public void Track(string sessionId, string jobId, ClaimsPrincipal principal)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(jobId))
        {
            return;
        }

        // Missing options match the product default: the stream is off, so there
        // is nowhere for notifications/progress to go.
        if (_options?.Value.ServerInitiatedStreamEnabled != true)
        {
            return;
        }

        Interlocked.Increment(ref _startedPolls);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobService = scope.ServiceProvider.GetRequiredService<IGeoprocessingJobService>();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _sessions.GetLifetimeToken(sessionId));
                linked.CancelAfter(MaxTrackingDuration);
                await PumpAsync(sessionId, jobId, principal, jobService, DefaultPollInterval, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                McpLog.ProgressBridgeStopped(_logger, sessionId, jobId, "cancelled");
            }
            // Intentionally generic: this is a fire-and-forget background poll; it must never
            // crash the host, so record the failure and stop.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                McpLog.ResourceReadFailed(_logger, "jobs", jobId, ex);
            }
        });
    }

    public async Task PumpAsync(
        string sessionId,
        string jobId,
        ClaimsPrincipal principal,
        IGeoprocessingJobService jobService,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        double? lastProgress = null;
        string? lastPhase = null;
        var first = true;

        while (!cancellationToken.IsCancellationRequested && _sessions.IsValid(sessionId))
        {
            ExecutionJobRecord record;
            try
            {
                record = await jobService.GetJobAsync(jobId, principal, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // Intentionally generic: the job vanished or the read failed; stop tracking rather
            // than spinning. The client can still read honua://jobs/{id} directly.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                McpLog.ProgressBridgeJobReadFailed(_logger, sessionId, jobId, ex);
                McpLog.ProgressBridgeStopped(_logger, sessionId, jobId, "job-unreadable");
                return;
            }

            var terminal = IsTerminal(record.Status);
            var phase = record.CurrentPhase ?? record.Status.ToString();
            var progress = record.PercentComplete ?? (terminal ? ProgressTotal : 0d);

            if (first || progress != lastProgress || !string.Equals(phase, lastPhase, StringComparison.Ordinal))
            {
                _publisher.PublishProgress(sessionId, jobId, progress, ProgressTotal, phase);
                lastProgress = progress;
                lastPhase = phase;
                first = false;
            }

            if (terminal)
            {
                McpLog.ProgressBridgeStopped(_logger, sessionId, jobId, record.Status.ToString());
                return;
            }

            if (interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status) =>
        status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;
}
