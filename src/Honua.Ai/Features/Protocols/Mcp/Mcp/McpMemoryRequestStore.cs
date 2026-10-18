// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>Single-host equivalent of the shared atomic request registry.</summary>
internal sealed class McpMemoryRequestStore(
    Func<string, string, bool> authorize, TimeProvider timeProvider) : IMcpRequestStore
{
    private sealed record Entry(string Principal, string Generation, DateTimeOffset ExpiresAt, bool Cancelled);
    private readonly Dictionary<(string Session, string Request), Entry> _entries = [];
    private readonly object _gate = new();

    public Task<McpRequestState> UpdateRequestAsync(
        string sessionId, string principalKey, string requestKey, string generation,
        McpRequestOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            {
                _entries.Remove(expired);
            }

            var key = (sessionId, requestKey);
            _entries.TryGetValue(key, out var entry);
            if (operation == McpRequestOperation.Finish)
            {
                // This compare/remove is the completion linearization point. A
                // delayed finish cannot remove a replacement generation.
                if (entry is null || entry.Generation != generation || entry.Principal != principalKey)
                {
                    return Task.FromResult(McpRequestState.Missing);
                }

                _entries.Remove(key);
                return Task.FromResult(!authorize(sessionId, principalKey) ? McpRequestState.Missing
                    : entry.Cancelled ? McpRequestState.Cancelled : McpRequestState.Active);
            }

            if (!authorize(sessionId, principalKey))
            {
                return Task.FromResult(McpRequestState.Missing);
            }

            if (operation == McpRequestOperation.Register)
            {
                if (entry is not null)
                {
                    return Task.FromResult(McpRequestState.Duplicate);
                }

                if (_entries.Count(pair => pair.Key.Session == sessionId) >= McpRequestBounds.PerSession
                    || _entries.Count(pair => pair.Value.Principal == principalKey) >= McpRequestBounds.PerPrincipal)
                {
                    return Task.FromResult(McpRequestState.Capacity);
                }

                _entries.Add(key, new Entry(principalKey, generation, now + McpRequestBounds.Lifetime, false));
                return Task.FromResult(McpRequestState.Active);
            }

            if (entry is null || entry.Principal != principalKey
                || (operation == McpRequestOperation.Read && entry.Generation != generation))
            {
                return Task.FromResult(McpRequestState.Missing);
            }

            if (operation == McpRequestOperation.Cancel)
            {
                _entries[key] = entry with { Cancelled = true };
                return Task.FromResult(McpRequestState.Cancelled);
            }

            return Task.FromResult(entry.Cancelled ? McpRequestState.Cancelled : McpRequestState.Active);
        }
    }
}
