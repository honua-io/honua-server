// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Configuration;

// Optimistic writes re-read after conflicts, so independent tasks never overwrite unseen changes.
internal sealed class ConfigurationCollectionStore<T>(
    PostgresConfigurationDocuments documents, ConfigurationCollection<T> collection, IHttpContextAccessor? contexts = null)
    where T : class
{
    private ConfigurationSnapshot<T>? _current;

    public Task<ConfigurationSnapshot<T>> ReadAsync(CancellationToken token) => documents.ReadAsync(collection, token);

    // Hot-path readers reuse the decrypted snapshot while the shared generation is unchanged. The request's
    // generation was captured before authentication; outside a request the generation row is read directly.
    public async Task<ConfigurationSnapshot<T>> ReadCurrentAsync(CancellationToken token)
    {
        var generation = contexts?.HttpContext?.Items[RequestConfigurationGeneration.ContextKey] is long captured
            ? captured
            : await documents.GetGenerationAsync(token).ConfigureAwait(false);
        var current = Volatile.Read(ref _current);
        if (current is not null && current.Generation == generation)
        {
            return current;
        }
        var snapshot = await ReadAsync(token).ConfigureAwait(false);
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }

    public async Task<T> CreateAsync(string key, T value, CancellationToken token)
    {
        if (!await documents.WriteAsync(collection, new(key, 0, value), token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Configuration already exists.");
        }
        return value;
    }

    public async Task<T?> MutateAsync(string key, Func<T?, T?> mutation, CancellationToken token)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var snapshot = await ReadAsync(token).ConfigureAwait(false);
            var current = snapshot.Documents.FirstOrDefault(document => document.Key == key);
            var next = mutation(current is null ? null : current.Value);
            if (ReferenceEquals(next, current?.Value))
            {
                return next;
            }
            if (await documents.WriteAsync(collection, new(key, current?.Revision ?? 0, next), token).ConfigureAwait(false))
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
            var current = (await ReadAsync(token).ConfigureAwait(false)).Documents.FirstOrDefault(document => document.Key == key);
            if (current is null) { return false; }
            if (await documents.WriteAsync(collection, new(key, current.Revision, default), token).ConfigureAwait(false)) { return true; }
        }
        throw new InvalidOperationException("Concurrent configuration updates exceeded the retry limit.");
    }
}
