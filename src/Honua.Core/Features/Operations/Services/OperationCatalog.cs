// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Default <see cref="IOperationCatalog"/>: aggregates every registered
/// <see cref="IOperationDescriptorProvider"/> into a versioned grounding snapshot. Mirrors
/// the workflow node registry's aggregation: providers are queried, descriptors are sorted
/// for stable output, and a fingerprint version is computed over the descriptor ids.
/// </summary>
public sealed class OperationCatalog : IOperationCatalog, IDisposable
{
    // Every operation dispatch calls GetDescriptorAsync, which previously rebuilt the
    // whole catalog (querying every provider, re-sorting, re-hashing a version string)
    // on every single call. Providers are typically static in-memory descriptor lists, so
    // a short absolute-expiry cache avoids redundant rebuilds under dispatch bursts while
    // still picking up provider changes (e.g. a future dynamic provider) within a bounded
    // staleness window — capability/grounding metadata tolerates a few seconds of staleness
    // far better than it tolerates rebuilding on every dispatch.
    private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IOperationDescriptorProvider> _providers;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private OperationCatalogSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAt;

    /// <summary>
    /// Initializes a new instance of <see cref="OperationCatalog"/>.
    /// </summary>
    /// <param name="providers">Registered descriptor providers.</param>
    /// <param name="clock">Time provider for snapshot timestamps.</param>
    public OperationCatalog(IEnumerable<IOperationDescriptorProvider> providers, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(clock);
        _providers = providers.ToArray();
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<OperationCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cachedSnapshot;
        if (cached is not null && _clock.GetUtcNow() - _cachedAt < SnapshotCacheTtl)
        {
            return cached;
        }

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have refreshed the snapshot while we waited.
            cached = _cachedSnapshot;
            if (cached is not null && _clock.GetUtcNow() - _cachedAt < SnapshotCacheTtl)
            {
                return cached;
            }

            var snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _cachedSnapshot = snapshot;
            _cachedAt = _clock.GetUtcNow();
            return snapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task<OperationCatalogSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var descriptors = new List<OperationDescriptor>();
        var providerIds = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            providerIds.Add(provider.ProviderId);
            var contributed = await provider.ListDescriptorsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var descriptor in contributed)
            {
                descriptors.Add(ToRecord(descriptor));
            }
        }

        var sorted = descriptors
            .OrderBy(descriptor => descriptor.Category, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();

        return new OperationCatalogSnapshot
        {
            CatalogVersion = ComputeVersion(sorted),
            GeneratedAt = _clock.GetUtcNow(),
            ProviderIds = providerIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Operations = sorted
        };
    }

    /// <inheritdoc />
    public async Task<OperationDescriptor?> GetDescriptorAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Operations.FirstOrDefault(operation =>
            string.Equals(operation.OperationId, operationId, StringComparison.Ordinal));
    }

    private static OperationDescriptor ToRecord(IOperationDescriptor descriptor)
        => descriptor as OperationDescriptor ?? new OperationDescriptor
        {
            OperationId = descriptor.OperationId,
            ProviderId = descriptor.ProviderId,
            Title = descriptor.Title,
            Description = descriptor.Description,
            Category = descriptor.Category,
            InputSchema = descriptor.InputSchema,
            OutputSchema = descriptor.OutputSchema,
            ExecutionKind = descriptor.ExecutionKind,
            ApprovalModel = descriptor.ApprovalModel,
            Policy = descriptor.Policy,
            Grounding = descriptor.Grounding
        };

    private static string ComputeVersion(IReadOnlyList<OperationDescriptor> descriptors)
    {
        var builder = new StringBuilder();
        foreach (var descriptor in descriptors)
        {
            builder.Append(descriptor.OperationId)
                .Append(':')
                .Append(descriptor.ProviderId)
                .Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "operation-catalog:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Disposes the snapshot-cache lock. The catalog is registered as a singleton and owns the
    /// <see cref="SemaphoreSlim"/> guarding its cached snapshot, so it must dispose it (CA1001).
    /// </summary>
    public void Dispose() => _snapshotLock.Dispose();
}
