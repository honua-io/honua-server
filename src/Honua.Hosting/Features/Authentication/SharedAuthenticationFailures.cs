// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal readonly record struct SharedFailureWindow(int Failures, DateTimeOffset? RefusedUntil, DateTimeOffset ExpiresAt)
{
    public DateTimeOffset? PreviousRefusal { get; init; }

    public bool IsRefused(DateTimeOffset now) => RefusedUntil > now;
}

internal readonly record struct LedgerFailureWindow(int Failures, TimeSpan? RefusalRemaining, TimeSpan WindowRemaining)
{
    internal SharedFailureWindow At(DateTimeOffset now)
        => new(Failures, RefusalRemaining is { } duration ? now + duration : null, now + WindowRemaining);
}

internal readonly record struct AuthenticationFailurePolicy(int Threshold, TimeSpan Window, TimeSpan BlockDuration);

internal readonly record struct PendingAuthenticationFailure(string Source, DateTimeOffset At);

internal sealed record AuthenticationFailure(byte[] SourceDigest, AuthenticationFailurePolicy Policy);

// The deployment-wide failure windows that every server instance counts into. Sources are keyed by a SHA-256 digest.
internal interface IAuthenticationFailureLedger
{
    // A null result leaves counting local while every shared slot has an active window.
    Task<LedgerFailureWindow?> RecordFailureAsync(AuthenticationFailure failure, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>> ReadRefusalsAsync(CancellationToken cancellationToken);
}

// What this instance knows of the shared windows: the refusals it polled and the totals its own failures returned.
// Requests only read this view; the sync service is the one that talks to the ledger.
internal sealed class SharedAuthenticationFailures(
    IAuthenticationFailureLedger ledger, IOptions<AuthenticationFailureLimiterOptions> options, TimeProvider timeProvider)
{
    private const int PendingCapacity = 4096;

    private readonly Channel<PendingAuthenticationFailure> _recorded = Channel.CreateBounded<PendingAuthenticationFailure>(
        new BoundedChannelOptions(PendingCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, SharedFailureWindow> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _pendingFailures = new(StringComparer.Ordinal);
    private readonly int _maxTrackedSources = options.Value.MaxTrackedSources;
    private readonly TimeSpan _windowLength = options.Value.Window;
    private readonly DateTimeOffset _clockOrigin = timeProvider.GetUtcNow();
    private readonly long _clockTimestamp = timeProvider.GetTimestamp();

    internal DateTimeOffset Now => _clockOrigin + timeProvider.GetElapsedTime(_clockTimestamp);

    internal IAuthenticationFailureLedger Ledger => ledger;

    internal AuthenticationFailureLimiterOptions Options => options.Value;

    internal ChannelReader<PendingAuthenticationFailure> Recorded => _recorded.Reader;

    public bool Record(string source, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_recorded.Writer.TryWrite(new(source, at)))
            {
                return false;
            }

            if (!_pendingFailures.TryGetValue(source, out var pending))
            {
                pending = new Queue<DateTimeOffset>();
                _pendingFailures.Add(source, pending);
            }

            pending.Enqueue(at);
            return true;
        }
    }

    internal void Complete() => _recorded.Writer.TryComplete();

    public SharedFailureWindow? For(string source, DateTimeOffset now)
        => FindWindow(Key(Digest(source)), now);

    internal (SharedFailureWindow? Window, int PendingFailures, DateTimeOffset? PreviousRefusal) ForAdmission(
        string source, DateTimeOffset now)
    {
        var key = Key(Digest(source));
        lock (_gate)
        {
            _windows.TryGetValue(key, out var window);
            var pendingCount = _pendingFailures.TryGetValue(source, out var pending)
                ? pending.Count(at => now - at < _windowLength)
                : 0;
            return (window.ExpiresAt > now ? window : null,
                pendingCount,
                window.RefusedUntil ?? window.PreviousRefusal);
        }
    }

    private SharedFailureWindow? FindWindow(string key, DateTimeOffset now)
        => _windows.TryGetValue(key, out var window) && window.ExpiresAt > now ? window : null;

    internal void Acknowledge(
        string source, (byte[] Digest, SharedFailureWindow Window)? persisted, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pendingFailures.TryGetValue(source, out var pending) && pending.TryDequeue(out _) && pending.Count == 0)
            {
                _pendingFailures.Remove(source);
            }

            if (persisted is { } result)
            {
                ApplyWindow(Key(result.Digest), result.Window, now);
            }
        }
    }

    internal void Apply(byte[] digest, SharedFailureWindow window, DateTimeOffset now)
    {
        lock (_gate)
        {
            ApplyWindow(Key(digest), window, now);
        }
    }

    private void ApplyWindow(string key, SharedFailureWindow window, DateTimeOffset now)
    {
        if (window.ExpiresAt <= now)
        {
            return;
        }

        // A poll and a write can complete out of order. Keep an active refusal until it expires.
        if (_windows.TryGetValue(key, out var current))
        {
            if (current.IsRefused(now) && current.RefusedUntil > window.RefusedUntil.GetValueOrDefault())
            {
                return;
            }

            window = window with { PreviousRefusal = current.RefusedUntil ?? current.PreviousRefusal };
        }
        else if (_windows.Count >= _maxTrackedSources)
        {
            if (!window.IsRefused(now))
            {
                return;
            }

            // Refusals take precedence over counters and expired refusal history.
            var evictable = _windows.Where(entry => !entry.Value.IsRefused(now))
                .OrderBy(entry => entry.Value.ExpiresAt).FirstOrDefault();
            if (evictable.Key is not null)
            {
                _windows.TryRemove(evictable);
            }
        }

        _windows[key] = window;
    }

    internal void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            var lastRefusal = window.RefusedUntil ?? window.PreviousRefusal;
            if (window.ExpiresAt <= now && (lastRefusal is null || now - lastRefusal >= Options.Window))
            {
                _windows.TryRemove(new KeyValuePair<string, SharedFailureWindow>(key, window));
            }
        }
    }

    internal static byte[] Digest(string source) => SHA256.HashData(Encoding.UTF8.GetBytes(source));

    private static string Key(byte[] digest) => Convert.ToHexString(digest);
}
