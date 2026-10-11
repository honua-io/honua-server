// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Stable content hash for a workflow package graph. Version creation compares this
/// hash under the package-row lock so a draft that changed after validation cannot
/// be stored under the hash of a different graph.
/// </summary>
public static class WorkflowPackageGraphHasher
{
    /// <summary>
    /// Computes the lowercase hex SHA-256 of the canonical graph content.
    /// </summary>
    public static string Compute(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var builder = new StringBuilder();
        builder.Append(graph.SchemaVersion).Append('|').Append(graph.WorkerProfile).Append('|');
        foreach (var node in graph.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            builder.Append("node:")
                .Append(node.NodeId)
                .Append(':')
                .Append(node.NodeTypeId)
                .Append(':')
                .Append(node.WorkerProfile);

            foreach (var parameter in node.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                builder.Append(':').Append(parameter.Key).Append('=').Append(parameter.Value);
            }
        }

        foreach (var edge in graph.Edges
                     .OrderBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.Kind))
        {
            builder.Append("|edge:")
                .Append(edge.SourceNodeId)
                .Append("->")
                .Append(edge.TargetNodeId)
                .Append(':')
                .Append(edge.Kind)
                .Append(':')
                .Append(edge.SourcePort)
                .Append(':')
                .Append(edge.TargetPort);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
