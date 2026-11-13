// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Logging;
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

// Process-local store; the interface leaves room for a shared one. A success never clears failures: they expire with the window.
internal sealed class AuthenticationFailureLimiter(
    IOptions<AuthenticationFailureLimiterOptions> options,
    TimeProvider timeProvider,
    ILogger<AuthenticationFailureLimiter> logger) : IAuthenticationFailureLimiter
{
    private static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(1);

    private readonly AuthenticationFailureLimiterOptions _options = options.Value;
    private readonly Dictionary<string, FailureWindow> _sources = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public TimeSpan? RefusalFor(string source)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            return _sources.TryGetValue(source, out var window) && window.RefusedUntil is { } until && until > now
                ? until - now
                : null;
        }
    }

    // An in-flight credential holds a slot, so a parallel burst of distinct guesses cannot pass the threshold; the same credential in flight shares one slot.
    public AuthenticationAttemptAdmission TryBeginAttempt(AuthenticationAttemptKey key)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            var window = Track(key.Source, now);
            if (window.RefusedUntil is { } until && until > now)
            {
                return AuthenticationAttemptAdmission.Refuse(until - now);
            }

            if (!window.Pending.ContainsKey(key.Credential) && window.Failures + window.Pending.Count >= _options.Threshold)
            {
                return AuthenticationAttemptAdmission.Refuse(InFlightRetryAfter);
            }

            window.Pending[key.Credential] = window.Pending.GetValueOrDefault(key.Credential) + 1;
            return AuthenticationAttemptAdmission.Admit;
        }
    }

    public void EndAttempt(AuthenticationAttemptKey key, AuthenticationAttemptOutcome outcome)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_sources.TryGetValue(key.Source, out var window))
            {
                return;
            }

            window.Release(key.Credential);
            if (outcome != AuthenticationAttemptOutcome.Failed || window.IsRefused(now))
            {
                ForgetIfIdle(key.Source, window, now);
                return;
            }

            if (++window.Failures < _options.Threshold)
            {
                return;
            }

            window.RefusedUntil = now + _options.BlockDuration;
        }

        AuthenticationLog.AuthenticationSourceBlocked(
            logger, LogValueRedactor.Hash(key.Source), (int)_options.BlockDuration.TotalSeconds);
    }

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
