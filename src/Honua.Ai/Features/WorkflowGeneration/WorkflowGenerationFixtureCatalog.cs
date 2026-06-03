// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Loads the deterministic workflow-generation fixtures embedded from
/// <c>tests/fixtures/ai-builder/workflow-generation-contract-v1.json</c>. Each scenario
/// pairs a prompt with a canned generation proposal; the catalog parses them once at
/// construction and exposes lookup by normalized prompt so
/// <see cref="DeterministicWorkflowGenerationProvider"/> can replay them without a model.
/// </summary>
internal sealed class WorkflowGenerationFixtureCatalog
{
    private const string ResourceName = "Honua.Ai.AiBuilder.Fixtures.workflow-generation-contract-v1.json";

    private readonly Dictionary<string, WorkflowGenerationModelProposal> _proposalsByPrompt;

    public WorkflowGenerationFixtureCatalog()
    {
        var proposals = new Dictionary<string, WorkflowGenerationModelProposal>(StringComparer.Ordinal);

        var assembly = typeof(WorkflowGenerationFixtureCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Workflow-generation fixture resource '{ResourceName}' is missing from the assembly. "
                + "Confirm the EmbeddedResource entry in Honua.Ai.csproj is correct.");

        using var document = JsonDocument.Parse(stream);
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var prompt = scenario.GetProperty("prompt").GetString() ?? string.Empty;
            if (!scenario.TryGetProperty("generation", out var generation)
                || generation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var proposal = generation.Deserialize(WorkflowGenerationJsonContext.Default.WorkflowGenerationModelProposal);
            if (proposal is null)
            {
                continue;
            }

            proposals[Normalize(prompt)] = proposal;
        }

        _proposalsByPrompt = proposals;
    }

    /// <summary>Normalizes a prompt for matching (trim + lowercase).</summary>
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Finds the canned proposal for the supplied prompt, or null when no fixture matches.
    /// </summary>
    public WorkflowGenerationModelProposal? FindByPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        return _proposalsByPrompt.TryGetValue(Normalize(prompt), out var proposal) ? proposal : null;
    }
}
