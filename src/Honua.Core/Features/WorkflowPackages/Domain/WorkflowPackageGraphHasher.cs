// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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

        // Length-prefix every field. Raw ':' and '=' inside parameter values must not
        // compare equal to a different parameter split (for example "a=b:c=d" versus a=b, c=d).
        var builder = new StringBuilder();
        AppendField(builder, graph.SchemaVersion);
        AppendField(builder, graph.WorkerProfile);
        foreach (var node in graph.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            AppendField(builder, node.NodeId);
            AppendField(builder, node.NodeTypeId);
            AppendField(builder, node.WorkerProfile);
            var parameters = node.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
            builder.Append(parameters.Length.ToString(CultureInfo.InvariantCulture)).Append(';');
            foreach (var parameter in parameters)
            {
                AppendField(builder, parameter.Key);
                AppendField(builder, parameter.Value);
            }
        }

        foreach (var edge in graph.Edges
                     .OrderBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.Kind))
        {
            AppendField(builder, edge.SourceNodeId);
            AppendField(builder, edge.TargetNodeId);
            AppendField(builder, edge.Kind.ToString());
            AppendField(builder, edge.SourcePort);
            AppendField(builder, edge.TargetPort);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder builder, string? value)
    {
        var text = value ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(text);
        builder.Append(';');
    }
}
