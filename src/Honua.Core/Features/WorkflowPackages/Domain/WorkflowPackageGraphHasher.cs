// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.WorkflowPackages.Domain;

// Version creation compares this hash under the package-row lock so a draft that changed after validation cannot be stored under another graph's hash.
public static class WorkflowPackageGraphHasher
{
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

        if (graph.Nodes.Any(node => node.HasExecutionSettings()))
        {
            AppendField(builder, "workflow-execution.v1");
            foreach (var node in graph.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
            {
                AppendField(builder, node.NodeId);
                AppendExecutionSettings(builder, node);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendExecutionSettings(StringBuilder builder, WorkflowNode node)
    {
        AppendField(builder, node.FailurePolicy.ToString());
        AppendField(builder, node.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, node.RetryPolicy is null ? null : "retry");
        if (node.RetryPolicy is { } retry)
        {
            AppendField(builder, retry.MaxAttempts.ToString(CultureInfo.InvariantCulture));
            AppendField(builder, retry.InitialDelaySeconds.ToString(CultureInfo.InvariantCulture));
            AppendField(builder, retry.BackoffMultiplier.ToString("R", CultureInfo.InvariantCulture));
            AppendField(builder, retry.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture));
        }
        AppendField(builder, node.Condition is null ? null : "condition");
        if (node.Condition is { } condition)
        {
            AppendField(builder, condition.SourceStepId);
            AppendField(builder, condition.Kind.ToString());
            AppendField(builder, condition.Threshold.ToString(CultureInfo.InvariantCulture));
            AppendField(builder, condition.ArtifactLabel);
            AppendField(builder, condition.Negate.ToString());
        }
        AppendField(builder, node.ForEach is null ? null : "forEach");
        if (node.ForEach is { } forEach)
        {
            AppendField(builder, forEach.ItemPlaceholder);
            AppendField(builder, forEach.MaxIterations?.ToString(CultureInfo.InvariantCulture));
            AppendField(builder, forEach.Items?.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var item in forEach.Items ?? [])
            {
                AppendField(builder, item);
            }
        }
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
