// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// Internal credentials contain identifiers or one-way hashes, never bearer material.
internal sealed record LiveStreamCredential(string Kind, string Subject);

internal sealed record LiveStreamIdentity(string? Principal, LiveStreamCredential? Credential,
    LiveStreamCredential? BackingCredential = null);

internal sealed record LiveStreamInvalidation(string Kind, string Subject, string? CredentialKind = null)
{
    internal bool Matches(LiveStreamIdentity identity) => Kind switch
    {
        "policy-changed" => true,
        "principal-changed" => string.Equals(Subject, identity.Principal, StringComparison.Ordinal),
        "credential-revoked" when CredentialKind == "PortalPrincipalRevocation" => identity.Principal is { } principal
            && string.Equals(Subject, Hash(principal), StringComparison.Ordinal),
        "credential-revoked" => MatchesCredential(identity.Credential) || MatchesCredential(identity.BackingCredential),
        _ => throw new InvalidOperationException("Unknown live-stream security invalidation.")
    };
    private bool MatchesCredential(LiveStreamCredential? credential) => credential is not null
        && string.Equals(CredentialKind, credential.Kind, StringComparison.Ordinal)
        && string.Equals(Subject, credential.Subject, StringComparison.Ordinal);

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class LiveStreamSecurityOptions
{
    public TimeSpan SecurityBackstopInterval { get; set; } = TimeSpan.FromSeconds(30);
}

// Every refresh is awaited before the security consumer permits later dispatch. Per-stream
// serialization also covers the backstop, so an older policy lookup cannot replace a newer one.
internal sealed class LiveStreamSecurityRegistry(
    IOptions<LiveStreamSecurityOptions>? options = null) : BackgroundService
{
    private readonly ConcurrentDictionary<long, Registration> _registrations = new();
    private long _nextId;

    internal Registration Register(LiveStreamIdentity identity, Func<CancellationToken, Task> refresh,
        Func<CancellationToken, Task>? revoke = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var registration = new Registration(identity, new(refresh, revoke), () => _registrations.TryRemove(id, out _));
        _registrations[id] = registration;
        return registration;
    }

    internal Task RefreshAsync(LiveStreamInvalidation? invalidation, CancellationToken cancellationToken)
    {
        return Parallel.ForEachAsync(_registrations.Values,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (registration, token) =>
            {
                if (invalidation?.Kind == "credential-revoked" && invalidation.Matches(registration.Identity))
                {
                    await registration.RevokeAsync(token).ConfigureAwait(false);
                }
                else if (invalidation is null || invalidation.Matches(registration.Identity))
                {
                    await registration.RefreshAsync(token).ConfigureAwait(false);
                }
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options?.Value.SecurityBackstopInterval ?? TimeSpan.FromSeconds(30);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(null, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    internal sealed record Callbacks(Func<CancellationToken, Task> Refresh, Func<CancellationToken, Task>? Revoke);

    internal sealed class Registration(
        LiveStreamIdentity identity,
        Callbacks callbacks,
        Action remove) : IDisposable, IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _disposed;
        internal LiveStreamIdentity Identity { get; } = identity;

        internal Task RefreshAsync(CancellationToken cancellationToken) => RunAsync(callbacks.Refresh, cancellationToken);

        // A stream without a revocation handler re-evaluates instead, which also fails closed.
        internal Task RevokeAsync(CancellationToken cancellationToken)
            => RunAsync(callbacks.Revoke ?? callbacks.Refresh, cancellationToken);

        private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    await action(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await _gate.WaitAsync().ConfigureAwait(false);
            _gate.Release();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            remove();
            // An already captured refresh may still be waiting on the semaphore.
        }
    }
}

// The endpoint attaches its policy refresh after it has resolved the admitted layer set.
internal sealed class LiveStreamPolicyRefreshFeature
{
    private Func<CancellationToken, Task<bool>>? _refresh;
    internal Func<Func<CancellationToken, Task<bool>>, CancellationToken, Task>? RevalidateAsync { get; set; }
    internal void SetRefresh(Func<CancellationToken, Task<bool>> refresh) => Volatile.Write(ref _refresh, refresh);
    internal Task<bool> RefreshAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _refresh)?.Invoke(cancellationToken) ?? Task.FromResult(true);
}

internal sealed record LiveStreamAdmissionFeature(string AuthenticationScheme, DateTimeOffset? ExpiresAt = null);

internal sealed record LiveStreamCredentialFeature(LiveStreamCredential Credential, DateTimeOffset ExpiresAt,
    LiveStreamCredential? BackingCredential = null)
{
    // Only the one-way hash of the presented secret is kept for later revocation matching.
    internal static LiveStreamCredentialFeature ForSecret(string kind, string secret, DateTimeOffset expiresAt)
        => new(new LiveStreamCredential(kind, LiveStreamInvalidation.Hash(secret)), expiresAt);
}

internal sealed class AllowAnonymousLiveStreamMetadata;
