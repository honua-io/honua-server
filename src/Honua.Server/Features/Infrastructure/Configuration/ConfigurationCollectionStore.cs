// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Configuration;

// Optimistic writes re-read after conflicts, so independent tasks never overwrite unseen changes.
internal sealed class ConfigurationCollectionStore<T>(
    PostgresConfigurationDocuments documents, ConfigurationCollection<T> collection, IHttpContextAccessor? contexts = null)
    where T : class
{
    private Cached? _current;
    private long _localWrites;

    public Task<ConfigurationSnapshot<T>> ReadAsync(CancellationToken token) => documents.ReadAsync(collection, token);

    // Hot-path readers reuse the decrypted snapshot while the shared generation is unchanged. The request's
    // generation was captured before authentication; outside a request the generation row is read directly.
    public async Task<ConfigurationSnapshot<T>> ReadCurrentAsync(CancellationToken token)
        => (await ReadCachedAsync(token).ConfigureAwait(false)).Snapshot;

    // The current documents by key, built once for each snapshot.
    public async Task<IReadOnlyDictionary<string, T>> ReadCurrentByKeyAsync(CancellationToken token)
        => (await ReadCachedAsync(token).ConfigureAwait(false)).ByKey;

    private async Task<Cached> ReadCachedAsync(CancellationToken token)
    {
        var generation = contexts?.HttpContext?.Items[RequestConfigurationGeneration.ContextKey] is long captured
            ? captured
            : await documents.GetGenerationAsync(token).ConfigureAwait(false);
        var localWrites = Interlocked.Read(ref _localWrites);
        var current = Volatile.Read(ref _current);
        if (current is not null && current.LocalWrites == localWrites && current.Snapshot.Generation == generation)
        {
            return current;
        }
        current = new(await ReadAsync(token).ConfigureAwait(false), localWrites);
        Volatile.Write(ref _current, current);
        return current;
    }

    public async Task<T> CreateAsync(string key, T value, CancellationToken token)
    {
        if (!await TryCreateAsync(key, value, token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Configuration already exists.");
        }
        return value;
    }

    public Task<bool> TryCreateAsync(string key, T value, CancellationToken token)
        => WriteAsync(new(key, 0, value), token);

    public async Task<T?> MutateAsync(string key, Func<T?, T?> mutation, CancellationToken token)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var current = await documents.FindAsync(collection, key, token).ConfigureAwait(false);
            var next = mutation(current is null ? null : current.Value);
            if (ReferenceEquals(next, current?.Value))
            {
                return next;
            }
            if (await WriteAsync(new(key, current?.Revision ?? 0, next), token).ConfigureAwait(false))
            {
                return next;
            }
        }
        throw new InvalidOperationException("Concurrent configuration updates exceeded the retry limit.");
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken token)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var current = await documents.FindAsync(collection, key, token).ConfigureAwait(false);
            if (current is null) { return false; }
            if (await WriteAsync(new(key, current.Revision, default), token).ConfigureAwait(false)) { return true; }
        }
        throw new InvalidOperationException("Concurrent configuration updates exceeded the retry limit.");
    }

    // The request that wrote must read its own change, and its captured generation is the one before the write.
    // A snapshot whose read began before the write is discarded even when it is stored after it.
    private async Task<bool> WriteAsync(ConfigurationWrite<T> write, CancellationToken token)
    {
        var written = await documents.WriteAsync(collection, write, token).ConfigureAwait(false);
        if (written)
        {
            Interlocked.Increment(ref _localWrites);
        }
        return written;
    }

    private sealed class Cached(ConfigurationSnapshot<T> snapshot, long localWrites)
    {
        private Dictionary<string, T>? _byKey;

        public ConfigurationSnapshot<T> Snapshot => snapshot;

        public long LocalWrites => localWrites;

        public IReadOnlyDictionary<string, T> ByKey => _byKey ??= snapshot.Documents.ToDictionary(
            static document => document.Key, static document => document.Value, StringComparer.Ordinal);
    }
}
