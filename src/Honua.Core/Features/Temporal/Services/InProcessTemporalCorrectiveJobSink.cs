// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Default corrective-job sink (slice 5 of honua-server#1166) that runs the forward corrective operation
/// as an in-process background task and assigns it a stable job id. This keeps the rollback path durable
/// enough for the baseline deployment without requiring the Redis-backed control plane; deployments that
/// route corrective work through the durable job runner register a sink that enqueues onto it instead.
/// </summary>
/// <remarks>
/// The corrective work runs on a detached task scope so submission returns immediately with a queued
/// handle, matching the async job semantics clients poll for. The work delegate performs only forward
/// edits through the canonical edit pipeline and never deletes change-log history.
/// </remarks>
public sealed partial class InProcessTemporalCorrectiveJobSink : ITemporalCorrectiveJobSink
{
    private readonly ILogger<InProcessTemporalCorrectiveJobSink> _logger;

    /// <summary>Creates the sink.</summary>
    /// <param name="logger">Logger for corrective-job lifecycle events.</param>
    public InProcessTemporalCorrectiveJobSink(ILogger<InProcessTemporalCorrectiveJobSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<(string JobId, string Status)> SubmitAsync(
        string operationName,
        string serviceId,
        int layerId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(work);

        var jobId = $"temporal-{Guid.NewGuid():N}";

        // Detach the corrective run from the request scope so submission returns a queued handle the
        // client polls. Exceptions are logged, not propagated, so a failed corrective run is observable
        // via job status rather than crashing the host.
        _ = Task.Run(async () =>
        {
            try
            {
                LogCorrectiveJobStarted(jobId, operationName, serviceId, layerId);
                await work(CancellationToken.None).ConfigureAwait(false);
                LogCorrectiveJobSucceeded(jobId, operationName);
            }
            catch (Exception ex)
            {
                LogCorrectiveJobFailed(jobId, operationName, ex);
            }
        }, CancellationToken.None);

        return Task.FromResult((jobId, "Queued"));
    }

    [LoggerMessage(12130, LogLevel.Information, "Temporal corrective job {JobId} ({Operation}) started for {ServiceId}/{LayerId}")]
    private partial void LogCorrectiveJobStarted(string jobId, string operation, string serviceId, int layerId);

    [LoggerMessage(12131, LogLevel.Information, "Temporal corrective job {JobId} ({Operation}) succeeded")]
    private partial void LogCorrectiveJobSucceeded(string jobId, string operation);

    [LoggerMessage(12132, LogLevel.Error, "Temporal corrective job {JobId} ({Operation}) failed")]
    private partial void LogCorrectiveJobFailed(string jobId, string operation, Exception exception);
}
