// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Threads geoprocessing job progress onto the owning MCP session's SSE stream as
/// <c>notifications/progress</c> frames (honua-server#1954). It does NOT fork the
/// job runtime: it polls the canonical <see cref="IGeoprocessingJobService"/> (the
/// same record the <c>honua://jobs/{id}</c> resource projects) and emits a
/// progress notification whenever the reported <c>PercentComplete</c> or current
/// phase changes, replacing client-side polling of <c>honua://jobs/{id}</c> with
/// a server push. The bridge stops when the job reaches a terminal status, the
/// session is terminated, or a safety cap elapses.
/// </summary>
internal sealed class McpJobProgressBridge
{
    /// <summary>Total used for the 0–100 completion ratio the runtime reports.</summary>
    private const double ProgressTotal = 100d;

    /// <summary>Default interval between job-status polls.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Safety cap on how long a single job is tracked, so a wedged or never-terminal
    /// job cannot leak a background poll for the lifetime of the process.
    /// </summary>
    private static readonly TimeSpan MaxTrackingDuration = TimeSpan.FromMinutes(30);

    private readonly IMcpNotificationPublisher _publisher;
    private readonly McpSessionManager _sessions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpJobProgressBridge> _logger;

    public McpJobProgressBridge(
        IMcpNotificationPublisher publisher,
        McpSessionManager sessions,
        IServiceScopeFactory scopeFactory,
        ILogger<McpJobProgressBridge> logger)
    {
        _publisher = publisher;
        _sessions = sessions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Starts a detached background poll that streams the job's progress to the
    /// session. Fire-and-forget: the HTTP request that submitted the job returns
    /// immediately while progress streams over the separate <c>GET /mcp</c> SSE
    /// channel. Exceptions are swallowed (logged) so a background poll can never
    /// surface on an unrelated request thread.
    /// </summary>
    public void Track(string sessionId, string jobId, ClaimsPrincipal principal)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(jobId))
        {
            return;
        }

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
            catch (Exception ex)
            {
                // A background poll must never crash the host; record and stop.
                McpLog.ResourceReadFailed(_logger, "jobs", jobId, ex);
            }
        });
    }

    /// <summary>
    /// Polls the job until it reaches a terminal status (or the token cancels) and
    /// pushes a progress notification on every observed change. Exposed for unit
    /// testing with a substitute job service and a zero poll interval. The first
    /// observed state is always emitted so the client sees an immediate frame.
    /// </summary>
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
            catch (Exception ex)
            {
                // The job vanished or the read failed; stop tracking rather than
                // spinning. The client can still read honua://jobs/{id} directly.
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
