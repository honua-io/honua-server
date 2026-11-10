// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Infrastructure.SharedCaching;

internal sealed partial class CdnInvalidationService(
    ICdnInvalidator invalidator,
    TimeProvider timeProvider,
    ILogger<CdnInvalidationService> logger,
    IHttpContextAccessor? httpContextAccessor = null)
{
    internal const string ResultHeader = "Honua-Cdn-Invalidation";

    // A layer that is edited every few seconds costs one invalidation per window, not one per edit; the lifetime is the backstop.
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CoalescedSubject> _subjects = new(StringComparer.OrdinalIgnoreCase);

    // Awaited by operations that reduce access: the caller does not report complete until the CDN was told.
    internal async Task<CdnInvalidationResult> InvalidateNowAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var result = await invalidator.InvalidateAsync([.. paths], cancellationToken).ConfigureAwait(false);
        ReportOnCurrentResponse(result);
        return result;
    }

    internal Task InvalidateCoalescedAsync(string subjectKey, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var subject = _subjects.GetOrAdd(subjectKey, static _ => new CoalescedSubject());
        var pathList = paths.ToArray();
        switch (subject.Admit(timeProvider.GetUtcNow(), out var delay))
        {
            case Admission.SendNow:
                return InvalidateNowAsync(pathList, cancellationToken);
            case Admission.SendWhenWindowCloses:
                _ = SendWhenWindowClosesAsync(subject, pathList, delay);
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task SendWhenWindowClosesAsync(CoalescedSubject subject, string[] paths, TimeSpan delay)
    {
        await Task.Delay(delay, timeProvider).ConfigureAwait(false);
        subject.MarkSent(timeProvider.GetUtcNow());
        var result = await invalidator.InvalidateAsync(paths, CancellationToken.None).ConfigureAwait(false);
        if (result.Failed)
        {
            Log.CoalescedInvalidationFailed(logger, result.Detail ?? "unknown");
        }
    }

    private void ReportOnCurrentResponse(CdnInvalidationResult result)
    {
        if (result.Outcome is CdnInvalidationOutcome.NotConfigured
            || httpContextAccessor?.HttpContext?.Response is not { HasStarted: false } response)
        {
            return;
        }

        response.Headers[ResultHeader] = result.Failed ? "failed" : "submitted";
    }

    private enum Admission
    {
        SendNow,
        SendWhenWindowCloses,
        AlreadyScheduled,
    }

    private sealed class CoalescedSubject
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
        private bool _scheduled;

        internal Admission Admit(DateTimeOffset now, out TimeSpan delay)
        {
            lock (_gate)
            {
                delay = _lastSent + CoalescingWindow - now;
                if (_scheduled)
                {
                    return Admission.AlreadyScheduled;
                }

                if (delay <= TimeSpan.Zero)
                {
                    _lastSent = now;
                    return Admission.SendNow;
                }

                _scheduled = true;
                return Admission.SendWhenWindowCloses;
            }
        }

        internal void MarkSent(DateTimeOffset now)
        {
            lock (_gate)
            {
                _lastSent = now;
                _scheduled = false;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10310,
            Level = LogLevel.Error,
            Message = "A coalesced CDN invalidation failed; cached entries expire with their lifetime. error={Error}")]
        public static partial void CoalescedInvalidationFailed(ILogger logger, string error);
    }
}

internal sealed class NoCdnInvalidator : ICdnInvalidator
{
    public Task<CdnInvalidationResult> InvalidateAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CdnInvalidationResult.NotConfigured);
}
