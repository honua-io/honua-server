// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// MVP in-memory catalog for Zarr registrations. Persistent storage is deferred
/// until a follow-on PR introduces a Postgres-backed catalog.
/// </summary>
internal sealed class InMemoryZarrStore : IZarrStore
{
    private readonly ConcurrentDictionary<long, ZarrRegistration> _registrations = new();
    private readonly ConcurrentDictionary<string, long> _uniqueKeys = new(StringComparer.Ordinal);
    private long _nextId;

    public Task<ZarrRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        _registrations.TryGetValue(id, out var registration);
        return Task.FromResult<ZarrRegistration?>(registration);
    }

    public Task<ZarrRegistration> RegisterAsync(ZarrRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = BuildUniqueKey(request.LayerId, request.Provider.ToString(), request.Bucket, request.RootPath);
        var id = Interlocked.Increment(ref _nextId);
        if (!_uniqueKeys.TryAdd(key, id))
        {
            throw new InvalidOperationException(
                $"A Zarr store is already registered for layer {request.LayerId} at {request.Provider}://{request.Bucket}/{request.RootPath}.");
        }

        var registration = new ZarrRegistration
        {
            Id = id,
            LayerId = request.LayerId,
            Name = request.Name,
            Description = request.Description,
            Provider = request.Provider,
            Bucket = request.Bucket,
            RootPath = request.RootPath,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _registrations[id] = registration;
        return Task.FromResult(registration);
    }

    public Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default)
    {
        if (!_registrations.TryRemove(id, out var existing))
        {
            return Task.FromResult(false);
        }
        var key = BuildUniqueKey(existing.LayerId, existing.Provider.ToString(), existing.Bucket, existing.RootPath);
        _uniqueKeys.TryRemove(key, out _);
        return Task.FromResult(true);
    }

    public Task<ZarrRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var results = _registrations.Values
            .Where(r => r.LayerId == layerId)
            .OrderByDescending(r => r.CreatedAt)
            .ToArray();
        return Task.FromResult(results);
    }

    public Task UpdateMetadataAsync(long id, ZarrStoreMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _registrations.AddOrUpdate(
            id,
            _ => throw new InvalidOperationException($"Zarr registration {id} does not exist."),
            (_, existing) => existing with
            {
                Metadata = metadata,
                MetadataScannedAt = DateTimeOffset.UtcNow
            });
        return Task.CompletedTask;
    }

    private static string BuildUniqueKey(int layerId, string provider, string bucket, string rootPath)
        => layerId + "|" + provider + "|" + bucket + "|" + rootPath;
}
