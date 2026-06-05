// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Fixture-replay workflow generation provider. Matches the incoming prompt against the
/// deterministic workflow-generation fixtures and replays the canned proposal. No network.
/// This is an explicit CI/eval mode, never a runtime default — it is only registered when
/// the configured default provider is <c>deterministic</c>.
/// </summary>
internal sealed class DeterministicWorkflowGenerationProvider : IWorkflowGenerationProvider
{
    private readonly WorkflowGenerationFixtureCatalog _catalog;
    private readonly ILogger<DeterministicWorkflowGenerationProvider> _logger;

    public DeterministicWorkflowGenerationProvider(
        WorkflowGenerationFixtureCatalog catalog,
        ILogger<DeterministicWorkflowGenerationProvider> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => WorkflowGenerationConfiguration.DeterministicProviderId;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public Task<WorkflowGenerationProposal> GenerateAsync(
        WorkflowGenerationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.workflowgen.generate");
        activity?.SetTag("workflowgen.provider", ProviderId);

        WorkflowGenerationLog.GenerationRequested(_logger, ProviderId, "deterministic-fixture");

        var fixture = _catalog.FindByPrompt(request.Prompt);
        if (fixture is null)
        {
            const string Reason = "No deterministic fixture entry matches the supplied prompt.";
            WorkflowGenerationLog.GenerationFailed(_logger, ProviderId, Reason);
            activity?.SetTag("workflowgen.success", false);
            return Task.FromResult(WorkflowGenerationProposal.Error(Reason, ProviderId, "deterministic-fixture"));
        }

        var proposal = WorkflowGenerationProposalMapper.ToProposal(
            fixture,
            ProviderId,
            "deterministic-fixture",
            usage: null);

        var nodeCount = proposal.Graph?.Nodes.Count ?? 0;
        WorkflowGenerationLog.GenerationProduced(_logger, proposal.Status, ProviderId, nodeCount);
        activity?.SetTag("workflowgen.success", true);
        activity?.SetTag("workflowgen.status", proposal.Status.ToString());

        return Task.FromResult(proposal);
    }
}
