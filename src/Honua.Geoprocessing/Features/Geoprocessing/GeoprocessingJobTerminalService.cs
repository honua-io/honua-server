// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Canonical bounded lifecycle projection over <see cref="IGeoprocessingJobService"/>.
/// Protocol adapters translate these outcomes and never coordinate stores, queues, workers,
/// notifiers, or remote compute backends directly.
/// </summary>
internal sealed class GeoprocessingJobTerminalService : IGeoprocessingJobTerminalService
{
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumPollDelay = TimeSpan.FromMilliseconds(500);

    private readonly IGeoprocessingJobService _jobs;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the production terminal service.</summary>
    public GeoprocessingJobTerminalService(
        IGeoprocessingJobService jobs,
        TimeProvider timeProvider)
        : this(jobs, timeProvider, (delay, token) => Task.Delay(delay, timeProvider, token))
    {
    }

    internal GeoprocessingJobTerminalService(
        IGeoprocessingJobService jobs,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _jobs = jobs;
        _timeProvider = timeProvider;
        _delay = delay;
    }

    public async Task<GeoprocessingTerminalWaitResult> WaitForTerminalAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default)
    {
        ValidateTimeout(timeout);
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            clientDisconnect,
            timeoutSource.Token);

        var delay = InitialPollDelay;
        try
        {
            while (true)
            {
                linkedSource.Token.ThrowIfCancellationRequested();
                var job = await _jobs.GetJobAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
                if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
                {
                    return new(GeoprocessingTerminalWaitOutcome.NotFound);
                }

                if (GeoprocessingJobService.IsTerminal(job.Status))
                {
                    return new(GeoprocessingTerminalWaitOutcome.Terminal, job);
                }

                await _delay(delay, linkedSource.Token).ConfigureAwait(false);
                delay = delay < MaximumPollDelay
                    ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumPollDelay.Ticks))
                    : MaximumPollDelay;
            }
        }
        catch (GeoprocessingNotFoundException)
        {
            return new(GeoprocessingTerminalWaitOutcome.NotFound);
        }
        catch (OperationCanceledException) when (clientDisconnect.IsCancellationRequested)
        {
            return new(GeoprocessingTerminalWaitOutcome.ClientDisconnected);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new(GeoprocessingTerminalWaitOutcome.Timeout);
        }
    }

    public async Task<GeoprocessingTerminalResult> WaitForResultAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var wait = await WaitForTerminalAsync(jobId, principal, timeout, clientDisconnect).ConfigureAwait(false);
        if (wait.Outcome != GeoprocessingTerminalWaitOutcome.Terminal)
        {
            var outcome = wait.Outcome switch
            {
                GeoprocessingTerminalWaitOutcome.NotFound => GeoprocessingTerminalResultOutcome.NotFound,
                GeoprocessingTerminalWaitOutcome.Timeout => GeoprocessingTerminalResultOutcome.Timeout,
                GeoprocessingTerminalWaitOutcome.ClientDisconnected => GeoprocessingTerminalResultOutcome.ClientDisconnected,
                _ => throw new InvalidOperationException($"Unexpected terminal wait outcome '{wait.Outcome}'.")
            };
            return new(outcome);
        }

        var job = wait.Job!;
        if (job.Status == ExecutionJobStatus.Failed)
        {
            return new(GeoprocessingTerminalResultOutcome.Failed, job);
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            return new(GeoprocessingTerminalResultOutcome.Cancelled, job);
        }

        var remaining = timeout - _timeProvider.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            return new(GeoprocessingTerminalResultOutcome.Timeout, job);
        }

        using var timeoutSource = new CancellationTokenSource(remaining, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            clientDisconnect,
            timeoutSource.Token);
        try
        {
            var package = await _jobs.GetJobResultsAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
            return new(GeoprocessingTerminalResultOutcome.Succeeded, job, package);
        }
        catch (GeoprocessingNotFoundException)
        {
            return new(GeoprocessingTerminalResultOutcome.NotFound, job);
        }
        catch (OperationCanceledException) when (clientDisconnect.IsCancellationRequested)
        {
            return new(GeoprocessingTerminalResultOutcome.ClientDisconnected, job);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new(GeoprocessingTerminalResultOutcome.Timeout, job);
        }
    }

    public async Task<GeoprocessingCancelResult> CancelAsync(
        string jobId,
        ClaimsPrincipal principal,
        TimeSpan timeout,
        CancellationToken clientDisconnect = default)
    {
        ValidateTimeout(timeout);
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            clientDisconnect,
            timeoutSource.Token);

        try
        {
            await _jobs.EnsureCallerAuthorizedAsync(
                principal,
                OperatorResourceType.Job,
                OperatorOperation.Execute,
                linkedSource.Token).ConfigureAwait(false);
            var current = await _jobs.GetJobAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
            if (current.Spec.Kind != ExecutionJobKind.Geoprocessing)
            {
                return new(GeoprocessingCancelOutcome.NotFound);
            }
            await _jobs.CancelJobAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
            var latest = await _jobs.GetJobAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
            return GeoprocessingJobService.IsTerminal(latest.Status) && latest.Status != ExecutionJobStatus.Cancelled
                ? new(GeoprocessingCancelOutcome.AlreadyTerminal, latest)
                : new(GeoprocessingCancelOutcome.Cancelled, latest);
        }
        catch (GeoprocessingCancellationUnsupportedException)
        {
            return new(GeoprocessingCancelOutcome.Unsupported);
        }
        catch (GeoprocessingCancellationUnconfirmedException)
        {
            return new(GeoprocessingCancelOutcome.Unconfirmed);
        }
        catch (GeoprocessingPreconditionFailedException)
        {
            // A completion/cancel race is terminal by definition. Re-read through the
            // ownership-enforcing service so adapters receive the winner without probing infra.
            try
            {
                var winner = await _jobs.GetJobAsync(jobId, principal, linkedSource.Token).ConfigureAwait(false);
                return GeoprocessingJobService.IsTerminal(winner.Status)
                    ? new(GeoprocessingCancelOutcome.AlreadyTerminal, winner)
                    : new(GeoprocessingCancelOutcome.Unconfirmed, winner);
            }
            catch (GeoprocessingNotFoundException)
            {
                return new(GeoprocessingCancelOutcome.NotFound);
            }
        }
        catch (GeoprocessingNotFoundException)
        {
            return new(GeoprocessingCancelOutcome.NotFound);
        }
        catch (OperationCanceledException) when (clientDisconnect.IsCancellationRequested)
        {
            return new(GeoprocessingCancelOutcome.ClientDisconnected);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new(GeoprocessingCancelOutcome.Timeout);
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A positive, finite timeout is required.");
        }
    }
}
