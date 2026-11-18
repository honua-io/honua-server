// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
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
        // is nowhere for notifications/message to go.
        if (_options?.Value.ServerInitiatedStreamEnabled != true)
        {
            return;
        }

        Interlocked.Increment(ref _startedPolls);
        _ = Task.Run(async () =>
        {
            try
            {
                using var watch = _sessions.AcquireSessionWatch(sessionId);
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

            var terminal = record.Status.IsTerminal();
            var phase = Phase(record);
            var progress = Progress(record);

            if (first || progress != lastProgress || !string.Equals(phase, lastPhase, StringComparison.Ordinal))
            {
                await PublishStatusAsync(sessionId, (jobId, record), cancellationToken).ConfigureAwait(false);
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

    // Job status goes out as notifications/message: the job outlives the tools/call that
    // started it, so there is no client progressToken a stock client would accept.
    private async Task PublishStatusAsync(
        string sessionId,
        (string JobId, ExecutionJobRecord Record) job,
        CancellationToken cancellationToken)
    {
        // A failed job is an error, so a session that lowered its level to errors still learns of it.
        var messageLevel = job.Record.Status == ExecutionJobStatus.Failed ? McpLogging.Error : McpLogging.Info;
        var level = await _sessions.GetLogLevelAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!McpLogging.IsEnabled(level, messageLevel))
        {
            return;
        }

        using var data = StatusData(job.JobId, job.Record);
        await _publisher.PublishMessage(sessionId, messageLevel, McpLogging.JobsLogger, data.RootElement.Clone())
            .ConfigureAwait(false);
    }

    private static JsonDocument StatusData(string jobId, ExecutionJobRecord record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jobId", jobId);
            writer.WriteString("uri", McpResourceUris.JobsPrefix + jobId);
            writer.WriteString("status", record.Status.ToString());
            writer.WriteString("phase", Phase(record));
            writer.WriteNumber("progress", Progress(record));
            writer.WriteNumber("total", ProgressTotal);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    private static string Phase(ExecutionJobRecord record) => record.CurrentPhase ?? record.Status.ToString();

    private static double Progress(ExecutionJobRecord record) =>
        record.PercentComplete ?? (record.Status.IsTerminal() ? ProgressTotal : 0d);
}
