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

internal sealed record AuthenticationFailure(byte[] SourceDigest, DateTimeOffset At, AuthenticationFailurePolicy Policy);

// The deployment-wide failure windows that every server instance counts into. Sources are keyed by a SHA-256 digest.
internal interface IAuthenticationFailureLedger
{
    // One counted failure: the window, the count and the refusal change in the one statement that returns them.
    Task<SharedFailureWindow> RecordFailureAsync(AuthenticationFailure failure, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyValuePair<byte[], SharedFailureWindow>>> ReadRefusalsAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

// What this instance knows of the shared windows: the refusals it polled and the totals its own failures returned.
// Requests only read this view; the sync service is the one that talks to the ledger.
internal sealed class SharedAuthenticationFailures(IAuthenticationFailureLedger ledger, IOptions<AuthenticationFailureLimiterOptions> options)
{
    private const int PendingCapacity = 4096;

    private readonly Channel<string> _recorded = Channel.CreateBounded<string>(
        new BoundedChannelOptions(PendingCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, SharedFailureWindow> _windows = new(StringComparer.Ordinal);
    private readonly int _maxTrackedSources = options.Value.MaxTrackedSources;

    internal IAuthenticationFailureLedger Ledger => ledger;

    internal AuthenticationFailureLimiterOptions Options => options.Value;

    internal ChannelReader<string> Recorded => _recorded.Reader;

    public void Record(string source) => _recorded.Writer.TryWrite(source);

    public SharedFailureWindow? For(string source, DateTimeOffset now)
        => _windows.TryGetValue(Key(Digest(source)), out var window) && window.ExpiresAt > now ? window : null;

    // A refusal is always kept; a plain count is dropped once the view is full, so the cap never costs a refusal.
    internal void Apply(byte[] digest, SharedFailureWindow window, DateTimeOffset now)
    {
        if (!window.IsRefused(now) && _windows.Count >= _maxTrackedSources)
        {
            return;
        }

        _windows[Key(digest)] = window;
    }

    internal void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            if (window.ExpiresAt <= now)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }

    internal static byte[] Digest(string source) => SHA256.HashData(Encoding.UTF8.GetBytes(source));

    private static string Key(byte[] digest) => Convert.ToHexString(digest);
}
