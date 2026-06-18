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
public sealed class OperationCatalog : IOperationCatalog
{
    private readonly IReadOnlyList<IOperationDescriptorProvider> _providers;
    private readonly TimeProvider _clock;

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
}
