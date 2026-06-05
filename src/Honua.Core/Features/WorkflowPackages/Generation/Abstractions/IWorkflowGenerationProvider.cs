// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Core.Features.WorkflowPackages.Generation.Abstractions;

/// <summary>
/// Generates a constrained workflow-package graph proposal from a natural-language prompt.
/// Implementations target specific LLM backends (OpenAI-compatible, Anthropic) or replay
/// deterministic fixtures, but always produce the same structured
/// <see cref="WorkflowGenerationProposal"/> output. The proposal is never trusted raw — the
/// host runs <see cref="Domain.WorkflowGenerationResult"/> validation as a hard gate.
/// </summary>
public interface IWorkflowGenerationProvider
{
    /// <summary>Stable provider id: <c>local</c>, <c>anthropic</c>, <c>openai</c>, or <c>deterministic</c>.</summary>
    string ProviderId { get; }

    /// <summary>True when the provider is configured and reachable (endpoint/key present, model set).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Generates a workflow-package graph proposal grounded in the supplied node whitelist.
    /// </summary>
    Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default);
}
