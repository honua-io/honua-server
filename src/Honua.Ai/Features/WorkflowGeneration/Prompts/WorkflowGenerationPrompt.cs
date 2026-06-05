// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Ai.WorkflowGeneration.Prompts;

/// <summary>
/// Builds the grounded system prompt for workflow generation. The prompt enumerates the
/// available node palette (whitelist) and the contract the model must follow, so a tuned
/// local model proposes only node types that exist on this server.
/// </summary>
internal static class WorkflowGenerationPrompt
{
    /// <summary>Builds the system prompt grounded in the registry snapshot.</summary>
    public static string BuildSystem(WorkflowGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's workflow author. You convert a natural-language request into a validated workflow.package graph for a geospatial server.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- You MUST only use node types from the palette below. The nodeTypeId field is constrained to these ids; never invent a node type.");
        sb.AppendLine("- Wire nodes with edges. Use kind \"Control\" for ordering and \"Data\" with a targetPort to bind an upstream output into a required parameter.");
        sb.AppendLine("- Satisfy every required parameter of a node either with a literal value in parameters or with an incoming Data edge whose targetPort is the parameter name.");
        sb.AppendLine("- The graph must be acyclic.");
        sb.AppendLine("- If a requested step has no matching node type, add it to unmappedRequests and set status \"unsupported\" with a capabilityState.");
        sb.AppendLine("- If the request is ambiguous (which layer, which target, schedule vs manual), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building a geospatial workflow, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the graph and a one-paragraph rationale.");
        sb.AppendLine("- Do not invent layout; the server assigns node positions.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous graph failed server validation. Fix these failures and return a corrected graph:");
            foreach (var failure in request.RepairFailures)
            {
                sb.Append("- [").Append(failure.Code).Append("] ").Append(failure.Message);
                if (!string.IsNullOrWhiteSpace(failure.FieldPath))
                {
                    sb.Append(" (at ").Append(failure.FieldPath).Append(')');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.Append("Node palette (registry ").Append(request.Registry.RegistryVersion).AppendLine("):");
        foreach (var node in request.Registry.Nodes)
        {
            AppendNode(sb, node);
        }

        return sb.ToString();
    }

    /// <summary>Builds the user message: the prompt plus any answered clarifications.</summary>
    public static string BuildUser(WorkflowGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(request.Prompt);

        if (request.Answers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Answers to prior clarifications:");
            foreach (var answer in request.Answers)
            {
                sb.Append("- ").Append(answer.QuestionId).Append(" = ").AppendLine(answer.OptionId);
            }
        }

        if (request.CurrentGraph is { Nodes.Count: > 0 })
        {
            sb.AppendLine();
            sb.Append("Refine the existing graph, which currently has ")
              .Append(request.CurrentGraph.Nodes.Count)
              .AppendLine(" node(s). Preserve existing nodes unless the request asks to change them.");
        }

        return sb.ToString();
    }

    private static void AppendNode(StringBuilder sb, WorkflowNodeDefinition node)
    {
        sb.Append("- ").Append(node.NodeTypeId).Append(" — ").Append(node.Title)
          .Append(" [").Append(node.Category).Append("]: ").AppendLine(node.Description);

        var required = node.ParameterSchemas.Where(p => p.Required).Select(p => p.Name).ToArray();
        if (required.Length > 0)
        {
            sb.Append("    required params: ").AppendLine(string.Join(", ", required));
        }
    }
}
