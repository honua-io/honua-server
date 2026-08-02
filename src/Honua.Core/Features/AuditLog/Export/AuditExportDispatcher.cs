// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// Tuning knobs for <see cref="AuditExportDispatcher"/> retry/backoff behavior.
/// </summary>
public sealed record AuditExportDispatcherOptions
{
    /// <summary>Maximum number of retry attempts after the initial send (default 3).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before the first retry; grows by <see cref="BackoffMultiplier"/> each attempt (default 200ms).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Exponential backoff multiplier applied to <see cref="BaseDelay"/> per retry (default 2.0).</summary>
    public double BackoffMultiplier { get; set; } = 2.0;
}

/// <summary>
/// Coordinates audit-event delivery to a single <see cref="IAuditSink"/> with
/// shared data-residency enforcement, retry/backoff, and dead-lettering (#2157).
/// </summary>
/// <remarks>
/// <para>
/// Delivery flow:
/// </para>
/// <list type="number">
/// <item>Residency is enforced first. A residency violation is <em>never</em>
/// retried — the batch goes straight to the dead-letter store and a permanent
/// failure is returned. The violation does not propagate out as an exception.</item>
/// <item>The sink is invoked; transient (retryable) failures are retried up to
/// <see cref="AuditExportDispatcherOptions.MaxRetries"/> with exponential backoff.</item>
/// <item>A permanent failure, or exhausting retries, dead-letters the full batch
/// (the tamper-evident chain is preserved) and returns the final failure.</item>
/// </list>
/// </remarks>
public sealed partial class AuditExportDispatcher
{
    private readonly AuditExportDispatcherOptions _options;
    private readonly IAuditDeadLetterStore _deadLetterStore;
    private readonly ILogger<AuditExportDispatcher> _logger;
    private readonly AuditResidencyGuard? _residencyGuard;

    /// <summary>
    /// Initializes a new dispatcher.
    /// </summary>
    /// <param name="options">Retry/backoff tuning.</param>
    /// <param name="deadLetterStore">Holding area for undeliverable batches.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="residencyGuard">Optional data-residency guard; when null, no residency restriction is applied.</param>
    public AuditExportDispatcher(
        AuditExportDispatcherOptions options,
        IAuditDeadLetterStore deadLetterStore,
        ILogger<AuditExportDispatcher> logger,
        AuditResidencyGuard? residencyGuard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadLetterStore);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _deadLetterStore = deadLetterStore;
        _logger = logger;
        _residencyGuard = residencyGuard;
    }

    /// <summary>
    /// Test/seam hook for the inter-attempt delay. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
    /// unit tests replace it so retries run without real wall-clock waits.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// Delivers a batch of audit events to the sink, applying residency,
    /// retry/backoff, and dead-lettering.
    /// </summary>
    /// <param name="sink">The destination connector.</param>
    /// <param name="events">The events to deliver.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The final delivery result.</returns>
    public async Task<AuditSinkResult> DispatchAsync(
        IAuditSink sink,
        IReadOnlyList<AuditEvent> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return AuditSinkResult.Success();
        }

        // 1. Residency first — a violation is never retried and never thrown out.
        if (_residencyGuard is not null && !_residencyGuard.IsAllowed(sink.Region))
        {
            var residencyError =
                $"Sink region '{sink.Region ?? "(none)"}' violates the configured data-residency allow-list.";
            LogResidencyViolation(sink.SinkType, sink.Region ?? "(none)", events.Count);
            await _deadLetterStore.StoreAsync(events, sink.SinkType, residencyError, ct).ConfigureAwait(false);
            return AuditSinkResult.PermanentFailure(residencyError);
        }

        // 2. Send with retry on retryable failures.
        AuditSinkResult result;
        var attempt = 0;
        while (true)
        {
            try
            {
                result = await sink.SendAsync(events, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sink contract is non-throwing; defensively treat an escaped
                // exception as a transient failure so it follows the retry path.
                LogSinkThrew(sink.SinkType, ex);
                result = AuditSinkResult.TransientFailure(ex.Message);
            }

            if (result.Succeeded)
            {
                if (attempt > 0)
                {
                    LogRecoveredAfterRetry(sink.SinkType, attempt);
                }

                return result;
            }

            // 3a. Permanent failure -> dead-letter without further retries.
            if (!result.Retryable)
            {
                break;
            }

            // 3b. Retryable but retries exhausted -> dead-letter.
            if (attempt >= _options.MaxRetries)
            {
                break;
            }

            var delay = ComputeDelay(attempt);
            LogTransientRetry(sink.SinkType, attempt + 1, _options.MaxRetries, result.Error ?? "(none)");
            attempt++;
            await DelayAsync(delay, ct).ConfigureAwait(false);
        }

        var finalError = result.Error ?? "Audit sink delivery failed.";
        LogDeadLettered(sink.SinkType, events.Count, finalError);
        await _deadLetterStore.StoreAsync(events, sink.SinkType, finalError, ct).ConfigureAwait(false);
        return result.Retryable ? AuditSinkResult.PermanentFailure(finalError) : result;
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var multiplier = Math.Pow(_options.BackoffMultiplier, attempt);
        var ticks = _options.BaseDelay.Ticks * multiplier;
        if (ticks <= 0 || double.IsNaN(ticks) || double.IsInfinity(ticks))
        {
            return TimeSpan.Zero;
        }

        // Clamp to a sane ceiling so a misconfigured multiplier cannot overflow.
        var clamped = Math.Min(ticks, TimeSpan.FromMinutes(5).Ticks);
        return TimeSpan.FromTicks((long)clamped);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Audit export to sink {SinkType} failed transiently (attempt {Attempt}/{MaxRetries}): {Error}")]
    partial void LogTransientRetry(string sinkType, int attempt, int maxRetries, string error);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Audit export to sink {SinkType} recovered after {Attempts} retr(y/ies).")]
    partial void LogRecoveredAfterRetry(string sinkType, int attempts);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Audit export to sink {SinkType} dead-lettered {EventCount} event(s): {Error}")]
    partial void LogDeadLettered(string sinkType, int eventCount, string error);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Audit export to sink {SinkType} blocked by data residency (region {Region}); dead-lettered {EventCount} event(s).")]
    partial void LogResidencyViolation(string sinkType, string region, int eventCount);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Audit export sink {SinkType} threw an unexpected exception; treating as a transient failure.")]
    partial void LogSinkThrew(string sinkType, Exception exception);
}
