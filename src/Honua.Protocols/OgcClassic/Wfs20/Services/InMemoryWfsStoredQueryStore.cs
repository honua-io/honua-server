// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

// The store of a host with no system database, which runs one node: the contract of the shared
// store, kept in this process.
internal sealed class InMemoryWfsStoredQueryStore : IWfsStoredQueryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Scope, string QueryId), string> _definitions = new(new KeyComparer());

    public Task<IReadOnlyList<string>> ListAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> definitions = _definitions
                .Where(pair => pair.Key.Scope == scope)
                .Select(pair => pair.Value)
                .ToArray();
            return Task.FromResult(definitions);
        }
    }

    public Task<string?> GetAsync(WfsStoredQueryKey key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_definitions.GetValueOrDefault((key.Scope, key.QueryId)));
        }
    }

    public Task<WfsStoredQueryCreateResult> TryCreateAsync(WfsStoredQueryEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (entry.Key.Scope, entry.Key.QueryId);
            if (_definitions.ContainsKey(key))
            {
                return Task.FromResult(WfsStoredQueryCreateResult.DuplicateId);
            }

            if (_definitions.Count(pair => pair.Key.Scope == entry.Key.Scope) >= IWfsStoredQueryStore.MaxPerScope)
            {
                return Task.FromResult(WfsStoredQueryCreateResult.ScopeFull);
            }

            _definitions.Add(key, entry.Definition);
            return Task.FromResult(WfsStoredQueryCreateResult.Created);
        }
    }

    public Task<bool> DeleteAsync(WfsStoredQueryKey key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_definitions.Remove((key.Scope, key.QueryId)));
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Scope, string QueryId)>
    {
        public bool Equals((string Scope, string QueryId) x, (string Scope, string QueryId) y)
            => x.Scope == y.Scope && StringComparer.OrdinalIgnoreCase.Equals(x.QueryId, y.QueryId);

        public int GetHashCode((string Scope, string QueryId) obj)
            => HashCode.Combine(obj.Scope, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.QueryId));
    }
}
