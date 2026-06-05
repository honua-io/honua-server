// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Core.Features.WorkflowPackages.Generation.Abstractions;

/// <summary>
/// Orchestrates natural-language workflow-package generation: grounds the prompt in the node
/// registry, selects a configured <see cref="IWorkflowGenerationProvider"/>, and runs the
/// <c>WorkflowPackageGraphValidator</c> as a hard gate so a generated graph is never returned to
/// the client unless it validates. The console binds to this through the two admin endpoints
/// (<c>POST /workflow-packages/generate</c> and <c>GET /workflow-generation/providers</c>).
/// </summary>
public interface IWorkflowGenerationService
{
    /// <summary>
    /// Describes whether generation is enabled and which providers are configured + usable.
    /// </summary>
    Task<WorkflowGenerationProviders> GetProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates (or refines) a workflow graph from a prompt. Returns a validated graph, a
    /// structured clarification request, or an unsupported/refused/error turn — never an
    /// unvalidated graph.
    /// </summary>
    Task<WorkflowGenerationResult> GenerateAsync(
        WorkflowGenerationRequest request,
        CancellationToken cancellationToken = default);
}
