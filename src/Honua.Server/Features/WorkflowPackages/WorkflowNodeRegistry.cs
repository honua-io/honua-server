// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class WorkflowNodeRegistry(
    IEnumerable<IWorkflowNodeProvider> providers,
    TimeProvider clock,
    ILogger<WorkflowNodeRegistry> logger) : IWorkflowNodeRegistry
{
    private readonly IReadOnlyList<IWorkflowNodeProvider> _providers = providers.ToArray();

    public async Task<WorkflowNodeRegistrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var nodes = new List<WorkflowNodeDefinition>();
        var providerSnapshots = new List<WorkflowNodeProviderSnapshot>();
        var warnings = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var providerNodes = await provider.ListNodesAsync(cancellationToken).ConfigureAwait(false);
                var providerWarnings = await provider.GetWarningsAsync(cancellationToken).ConfigureAwait(false);
                nodes.AddRange(providerNodes);
                warnings.AddRange(providerWarnings);
                providerSnapshots.Add(new WorkflowNodeProviderSnapshot
                {
                    ProviderId = provider.ProviderId,
                    Version = provider.Version,
                    Available = true,
                    Warnings = providerWarnings
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WorkflowPackageLog.NodeProviderFailed(logger, provider.ProviderId, ex);
                var warning = $"Node provider '{provider.ProviderId}' is unavailable.";
                warnings.Add(warning);
                providerSnapshots.Add(new WorkflowNodeProviderSnapshot
                {
                    ProviderId = provider.ProviderId,
                    Version = provider.Version,
                    Available = false,
                    Warnings = [warning]
                });
            }
        }

        var sortedNodes = nodes
            .OrderBy(node => node.Category, StringComparer.Ordinal)
            .ThenBy(node => node.NodeTypeId, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new WorkflowNodeRegistrySnapshot
        {
            RegistryVersion = ComputeRegistryVersion(providerSnapshots, sortedNodes),
            GeneratedAt = clock.GetUtcNow(),
            Providers = providerSnapshots
                .OrderBy(provider => provider.ProviderId, StringComparer.Ordinal)
                .ToArray(),
            Nodes = sortedNodes,
            Warnings = warnings
        };

        WorkflowPackageLog.NodeRegistryListed(logger, snapshot.Nodes.Count, snapshot.RegistryVersion);
        return snapshot;
    }

    public async Task<WorkflowNodeDefinition?> GetNodeAsync(
        string nodeTypeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeTypeId))
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.NodeTypeId, nodeTypeId, StringComparison.Ordinal));
    }

    private static string ComputeRegistryVersion(
        IReadOnlyList<WorkflowNodeProviderSnapshot> providers,
        IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        var builder = new StringBuilder();
        foreach (var provider in providers.OrderBy(provider => provider.ProviderId, StringComparer.Ordinal))
        {
            builder.Append(provider.ProviderId).Append(':').Append(provider.Version).Append(';');
        }

        foreach (var node in nodes)
        {
            builder.Append(node.NodeTypeId).Append(':').Append(node.ProcessId).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "workflow-node-registry:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
