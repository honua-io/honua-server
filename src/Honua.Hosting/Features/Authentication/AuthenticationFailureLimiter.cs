// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// Credential is a per-process randomized hash of the presented secret, so the store never holds the secret itself.
internal readonly record struct AuthenticationAttemptKey(string Source, int Credential);

internal readonly record struct AuthenticationAttemptAdmission(bool Admitted, TimeSpan RetryAfter)
{
    internal static AuthenticationAttemptAdmission Admit { get; } = new(true, TimeSpan.Zero);

    internal static AuthenticationAttemptAdmission Refuse(TimeSpan retryAfter) => new(false, retryAfter);
}

internal enum AuthenticationAttemptOutcome
{
    NotCounted,

    Failed,
}

internal interface IAuthenticationFailureLimiter
{
    TimeSpan? RefusalFor(string source);

    AuthenticationAttemptAdmission TryBeginAttempt(AuthenticationAttemptKey key);

    void EndAttempt(AuthenticationAttemptKey key, AuthenticationAttemptOutcome outcome);
}

// Counts in process; with a system database the shared view adds the refusals and totals of every instance.
// A success never clears failures: they expire with the window.
internal sealed class AuthenticationFailureLimiter(
    IOptions<AuthenticationFailureLimiterOptions> options,
    TimeProvider timeProvider,
    ILogger<AuthenticationFailureLimiter> logger) : IAuthenticationFailureLimiter
{
    private static readonly Counter<long> FailedAttempts = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua_authentication_failed_attempts_total",
        unit: null,
        description: "Completed failed credential checks across all tracked sources.");
    private static readonly Counter<long> SharedFailuresDropped = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua_authentication_shared_failures_dropped_total",
        unit: null,
        description: "Failures counted locally whose shared write could not be queued.");
    private static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(1);

    private readonly AuthenticationFailureLimiterOptions _options = options.Value;
    private readonly Dictionary<string, FailureWindow> _sources = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // Set by the host that has a system database; null counts per instance.
    public SharedAuthenticationFailures? Shared { get; init; }

    public TimeSpan? RefusalFor(string source)
    {
        var now = timeProvider.GetUtcNow();
        var until = Later(LocalRefusal(source, now), SharedRefusal(source, now));
        return until > now ? until - now : null;
    }

    // An in-flight credential holds a slot, so a parallel burst of distinct guesses cannot pass the threshold; the same credential in flight shares one slot.
    public AuthenticationAttemptAdmission TryBeginAttempt(AuthenticationAttemptKey key)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            var pendingSharedFailures = 0;
            var sharedWindow = Shared?.For(key.Source, now, out pendingSharedFailures);
            var window = Track(key.Source, now);
            if (Later(window.RefusedUntil, sharedWindow?.RefusedUntil) is { } until && until > now)
            {
                return AuthenticationAttemptAdmission.Refuse(until - now);
            }

            var failures = Math.Max(window.Failures, (long)(sharedWindow?.Failures ?? 0) + pendingSharedFailures);
            if (!window.Pending.ContainsKey(key.Credential) && failures + window.Pending.Count >= _options.Threshold)
            {
                return AuthenticationAttemptAdmission.Refuse(InFlightRetryAfter);
            }

            window.Pending[key.Credential] = window.Pending.GetValueOrDefault(key.Credential) + 1;
            return AuthenticationAttemptAdmission.Admit;
        }
    }

    public void EndAttempt(AuthenticationAttemptKey key, AuthenticationAttemptOutcome outcome)
    {
        if (outcome == AuthenticationAttemptOutcome.Failed)
        {
            FailedAttempts.Add(1);
        }

        var now = timeProvider.GetUtcNow();
        bool sharedWriteDropped;
        bool blocked;
        lock (_gate)
        {
            _sources.TryGetValue(key.Source, out var window);
            window?.Release(key.Credential);
            if (outcome != AuthenticationAttemptOutcome.Failed || window?.IsRefused(now) is true)
            {
                if (window is not null)
                {
                    ForgetIfIdle(key.Source, window, now);
                }

                return;
            }

            // The source entry can be evicted while an admitted check is in flight.
            sharedWriteDropped = Shared?.Record(key.Source, now) is false;
            blocked = window?.RecordFailure(now, _options) is true;
        }

        if (sharedWriteDropped)
        {
            SharedFailuresDropped.Add(1);
        }

        if (blocked)
        {
            AuthenticationLog.AuthenticationSourceBlocked(
                logger, LogValueRedactor.Hash(key.Source), (int)_options.BlockDuration.TotalSeconds);
        }
    }

    private DateTimeOffset? LocalRefusal(string source, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(source, out var window) && window.IsRefused(now) ? window.RefusedUntil : null;
        }
    }

    private DateTimeOffset? SharedRefusal(string source, DateTimeOffset now)
        => Shared?.For(source, now) is { } window && window.IsRefused(now) ? window.RefusedUntil : null;

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
        => first is null ? second : second is null ? first : first > second ? first : second;

    private FailureWindow Track(string source, DateTimeOffset now)
    {
        if (_sources.TryGetValue(source, out var existing) && !existing.IsWindowOver(now, _options.Window))
        {
            return existing;
        }

        if (existing is null && _sources.Count >= _options.MaxTrackedSources)
        {
            MakeRoom(now);
        }

        var window = new FailureWindow(now, existing?.Pending);
        _sources[source] = window;
        return window;
    }

    private void ForgetIfIdle(string source, FailureWindow window, DateTimeOffset now)
    {
        if (window.IsEvictable(now, _options.Window))
        {
            _sources.Remove(source);
        }
    }

    // Idle and expired entries go first; at the cap the oldest unrefused source is dropped so a refusal is the last to be lost.
    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var (source, window) in _sources)
        {
            ForgetIfIdle(source, window, now);
        }

        if (_sources.Count >= _options.MaxTrackedSources)
        {
            var oldest = _sources.MinBy(entry => (entry.Value.IsRefused(now), entry.Value.Started));
            _sources.Remove(oldest.Key);
        }
    }

    private sealed class FailureWindow(DateTimeOffset started, Dictionary<int, int>? pending)
    {
        public DateTimeOffset Started { get; } = started;

        public Dictionary<int, int> Pending { get; } = pending ?? [];

        public int Failures { get; set; }

        public DateTimeOffset? RefusedUntil { get; set; }

        public bool IsRefused(DateTimeOffset now) => RefusedUntil > now;

        // A window that ended in a refusal restarts when the refusal ends, not when the counting window would have.
        public bool IsWindowOver(DateTimeOffset now, TimeSpan length)
            => RefusedUntil is { } until ? until <= now : now - Started >= length;

        public bool IsEvictable(DateTimeOffset now, TimeSpan length)
            => Pending.Count == 0 && (IsWindowOver(now, length) || (Failures == 0 && RefusedUntil is null));

        public bool RecordFailure(DateTimeOffset now, AuthenticationFailureLimiterOptions options)
        {
            if (++Failures < options.Threshold)
            {
                return false;
            }

            RefusedUntil = now + options.BlockDuration;
            return true;
        }

        public void Release(int credential)
        {
            if (!Pending.TryGetValue(credential, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                Pending.Remove(credential);
            }
            else
            {
                Pending[credential] = count - 1;
            }
        }
    }
}
