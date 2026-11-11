// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Security;

// For a host with no system database, which is one process by definition, and for tests.
internal sealed class InMemorySecurityTokenStore(TimeProvider? timeProvider = null) : ISecurityTokenStore
{
    private readonly ConcurrentDictionary<SecurityTokenKey, StoredSecurityToken> _tokens = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task StoreAsync(SecurityTokenKey key, ReadOnlyMemory<byte> payload, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        _tokens[key] = new StoredSecurityToken(payload.ToArray(), _timeProvider.GetUtcNow().Add(lifetime));
        return Task.CompletedTask;
    }

    public Task<StoredSecurityToken?> FindAsync(SecurityTokenKey key, CancellationToken cancellationToken)
        => Task.FromResult(_tokens.TryGetValue(key, out var token) && IsLive(token) ? token : null);

    public Task<StoredSecurityToken?> ConsumeAsync(SecurityTokenKey key, CancellationToken cancellationToken)
        => Task.FromResult(_tokens.TryRemove(key, out var token) && IsLive(token) ? token : null);

    public Task RestoreAsync(SecurityTokenKey key, StoredSecurityToken token, CancellationToken cancellationToken)
    {
        _tokens[key] = token;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(SecurityTokenKey key, CancellationToken cancellationToken)
    {
        _tokens.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private bool IsLive(StoredSecurityToken token) => token.ExpiresAt > _timeProvider.GetUtcNow();
}
