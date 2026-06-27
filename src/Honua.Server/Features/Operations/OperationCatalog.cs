// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Aggregates every <see cref="IOperationProvider"/> into a versioned operation catalog
/// snapshot. Mirrors <see cref="Honua.Server.Features.WorkflowPackages.WorkflowNodeRegistry"/>
/// one level up.
/// </summary>
internal sealed class OperationCatalog(
    IEnumerable<IOperationProvider> providers,
    TimeProvider clock,
    ILogger<OperationCatalog> logger) : IOperationCatalog
{
    private readonly IReadOnlyList<IOperationProvider> _providers = providers.ToArray();

    public async Task<OperationCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var operations = new List<HonuaOperationDefinition>();
        var providerSnapshots = new List<OperationProviderSnapshot>();
        var warnings = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var providerOperations = await provider.ListOperationsAsync(cancellationToken).ConfigureAwait(false);
                operations.AddRange(providerOperations);
                providerSnapshots.Add(new OperationProviderSnapshot
                {
                    ProviderId = provider.ProviderId,
                    Available = true
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                OperationsLog.OperationProviderFailed(logger, provider.ProviderId, ex);
                var warning = $"Operation provider '{provider.ProviderId}' is unavailable.";
                warnings.Add(warning);
                providerSnapshots.Add(new OperationProviderSnapshot
                {
                    ProviderId = provider.ProviderId,
                    Available = false,
                    Warnings = [warning]
                });
            }
        }

        var sortedOperations = operations
            .OrderBy(operation => operation.Category, StringComparer.Ordinal)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new OperationCatalogSnapshot
        {
            CatalogVersion = ComputeCatalogVersion(providerSnapshots, sortedOperations),
            GeneratedAt = clock.GetUtcNow(),
            Providers = providerSnapshots
                .OrderBy(provider => provider.ProviderId, StringComparer.Ordinal)
                .ToArray(),
            Operations = sortedOperations,
            Warnings = warnings
        };

        OperationsLog.OperationCatalogListed(logger, snapshot.Operations.Count, snapshot.CatalogVersion);
        return snapshot;
    }

    public async Task<HonuaOperationDefinition?> GetOperationAsync(
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

    private static string ComputeCatalogVersion(
        IReadOnlyList<OperationProviderSnapshot> providers,
        IReadOnlyList<HonuaOperationDefinition> operations)
    {
        var builder = new StringBuilder();
        foreach (var provider in providers.OrderBy(provider => provider.ProviderId, StringComparer.Ordinal))
        {
            builder.Append(provider.ProviderId).Append(';');
        }

        foreach (var operation in operations)
        {
            builder.Append(operation.OperationId).Append(':').Append(operation.ProviderId).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "operation-catalog:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
