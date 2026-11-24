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
    public bool IsRefused(DateTimeOffset now) => RefusedUntil > now;
}

internal readonly record struct AuthenticationFailurePolicy(int Threshold, TimeSpan Window, TimeSpan BlockDuration);

internal readonly record struct PendingAuthenticationFailure(string Source, DateTimeOffset At);

internal sealed record AuthenticationFailure(byte[] SourceDigest, AuthenticationFailurePolicy Policy);

// The deployment-wide failure windows that every server instance counts into. Sources are keyed by a SHA-256 digest.
internal interface IAuthenticationFailureLedger
{
    // One counted failure: the window, the count and the refusal change in the one statement that returns them.
    Task<SharedFailureWindow> RecordFailureAsync(AuthenticationFailure failure, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyValuePair<byte[], SharedFailureWindow>>> ReadRefusalsAsync(
        DateTimeOffset now, CancellationToken cancellationToken);
}

// What this instance knows of the shared windows: the refusals it polled and the totals its own failures returned.
// Requests only read this view; the sync service is the one that talks to the ledger.
internal sealed class SharedAuthenticationFailures(IAuthenticationFailureLedger ledger, IOptions<AuthenticationFailureLimiterOptions> options)
{
    private const int PendingCapacity = 4096;

    private readonly Channel<PendingAuthenticationFailure> _recorded = Channel.CreateBounded<PendingAuthenticationFailure>(
        new BoundedChannelOptions(PendingCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, SharedFailureWindow> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingFailures = new(StringComparer.Ordinal);
    private readonly int _maxTrackedSources = options.Value.MaxTrackedSources;

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

            _pendingFailures[source] = _pendingFailures.GetValueOrDefault(source) + 1;
            return true;
        }
    }

    internal void Complete() => _recorded.Writer.TryComplete();

    public SharedFailureWindow? For(string source, DateTimeOffset now)
        => FindWindow(Key(Digest(source)), now);

    internal SharedFailureWindow? For(string source, DateTimeOffset now, out int pendingFailures)
    {
        var key = Key(Digest(source));
        lock (_gate)
        {
            pendingFailures = _pendingFailures.GetValueOrDefault(source);
            return FindWindow(key, now);
        }
    }

    private SharedFailureWindow? FindWindow(string key, DateTimeOffset now)
        => _windows.TryGetValue(key, out var window) && window.ExpiresAt > now ? window : null;

    internal void Acknowledge(string source, byte[] digest, SharedFailureWindow? window, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pendingFailures.GetValueOrDefault(source) > 1)
            {
                _pendingFailures[source]--;
            }
            else
            {
                _pendingFailures.Remove(source);
            }

            if (window is { } persisted)
            {
                ApplyWindow(Key(digest), persisted, now);
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

    // Always keep refusals. The cap only applies to new unrefused sources.
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
        }
        else if (!window.IsRefused(now) && _windows.Count >= _maxTrackedSources)
        {
            return;
        }

        _windows[key] = window;
    }

    internal void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            if (window.ExpiresAt <= now)
            {
                _windows.TryRemove(new KeyValuePair<string, SharedFailureWindow>(key, window));
            }
        }
    }

    internal static byte[] Digest(string source) => SHA256.HashData(Encoding.UTF8.GetBytes(source));

    private static string Key(byte[] digest) => Convert.ToHexString(digest);
}
