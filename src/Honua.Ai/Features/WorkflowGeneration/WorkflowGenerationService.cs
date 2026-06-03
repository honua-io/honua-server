// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Default <see cref="IWorkflowGenerationService"/>: grounds a prompt in the node registry, runs
/// the configured provider, and applies <see cref="WorkflowPackageGraphValidator"/> as a hard
/// gate (with a bounded repair re-prompt) so the client never receives an unvalidated graph.
/// </summary>
internal sealed class WorkflowGenerationService : IWorkflowGenerationService
{
    private readonly IReadOnlyList<IWorkflowGenerationProvider> _providers;
    private readonly IWorkflowNodeRegistry _registry;
    private readonly WorkflowGenerationConfiguration _configuration;
    private readonly ILogger<WorkflowGenerationService> _logger;

    public WorkflowGenerationService(
        IEnumerable<IWorkflowGenerationProvider> providers,
        IWorkflowNodeRegistry registry,
        IOptions<WorkflowGenerationConfiguration> options,
        ILogger<WorkflowGenerationService> logger)
    {
        _providers = providers.ToArray();
        _registry = registry;
        _configuration = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<WorkflowGenerationProviders> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled)
        {
            return Task.FromResult(new WorkflowGenerationProviders { Enabled = false });
        }

        var descriptors = _providers
            .GroupBy(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(provider =>
            {
                var options = _configuration.GetProvider(provider.ProviderId);
                return new WorkflowGenerationProviderDescriptor
                {
                    Id = provider.ProviderId,
                    Label = LabelFor(provider.ProviderId),
                    Kind = provider.ProviderId,
                    Available = provider.IsConfigured,
                    Detail = provider.IsConfigured
                        ? DescribeConfigured(provider.ProviderId, options)
                        : "Not configured."
                };
            })
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new WorkflowGenerationProviders
        {
            Enabled = descriptors.Any(descriptor => descriptor.Available),
            DefaultProvider = _configuration.DefaultProvider,
            Providers = descriptors
        });
    }

    /// <inheritdoc />
    public async Task<WorkflowGenerationResult> GenerateAsync(
        WorkflowGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_configuration.Enabled)
        {
            return Unsupported("AI workflow generation is disabled on this server.");
        }

        var providerId = string.IsNullOrWhiteSpace(request.ProviderId)
            ? _configuration.DefaultProvider
            : request.ProviderId;

        var provider = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) && candidate.IsConfigured);

        if (provider is null)
        {
            return Unsupported($"Workflow generation provider '{providerId}' is not configured on this server.");
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.workflowgen.orchestrate");
        activity?.SetTag("workflowgen.provider", provider.ProviderId);

        var snapshot = await _registry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var nodeDefinitions = BuildNodeIndex(snapshot.Nodes);

        var providerRequest = new WorkflowGenerationProviderRequest
        {
            Prompt = request.Prompt,
            ModelOverride = request.ModelOverride,
            Conversation = request.Conversation,
            Answers = request.Answers,
            CurrentGraph = request.CurrentGraph,
            Registry = snapshot
        };

        var proposal = await provider.GenerateAsync(providerRequest, cancellationToken).ConfigureAwait(false);

        WorkflowPackageValidationResult? validation = null;
        if (proposal.Status == WorkflowGenerationStatus.Generated && proposal.Graph is not null)
        {
            validation = WorkflowPackageGraphValidator.Validate(proposal.Graph, nodeDefinitions);

            // Bounded repair: feed the validator failures back to the provider and re-validate.
            var attempts = 0;
            while (!validation.IsValid && attempts < _configuration.MaxRepairAttempts)
            {
                attempts++;
                var repairRequest = providerRequest with { RepairFailures = validation.Failures };
                proposal = await provider.GenerateAsync(repairRequest, cancellationToken).ConfigureAwait(false);
                if (proposal.Status != WorkflowGenerationStatus.Generated || proposal.Graph is null)
                {
                    break;
                }

                validation = WorkflowPackageGraphValidator.Validate(proposal.Graph, nodeDefinitions);
            }

            // Hard gate: never hand back a graph the server's own validator rejects.
            if (proposal.Status == WorkflowGenerationStatus.Generated &&
                proposal.Graph is not null &&
                !validation.IsValid)
            {
                var summary = string.Join("; ", validation.Failures.Select(failure => failure.Message));
                WorkflowGenerationLog.GenerationRejected(_logger, provider.ProviderId, summary);
                return new WorkflowGenerationResult
                {
                    Status = WorkflowGenerationStatus.Error,
                    Rationale = "The proposed workflow did not pass server validation: " + summary,
                    Validation = validation,
                    Provider = proposal.ProviderId ?? provider.ProviderId,
                    Model = proposal.Model,
                    RegistryVersion = snapshot.RegistryVersion
                };
            }
        }

        return new WorkflowGenerationResult
        {
            Status = proposal.Status,
            Graph = proposal.Status == WorkflowGenerationStatus.Generated ? proposal.Graph : null,
            Rationale = proposal.Rationale ?? proposal.ErrorMessage,
            Clarifications = proposal.Clarifications,
            Validation = validation,
            UnmappedRequests = proposal.UnmappedRequests,
            CapabilityState = proposal.CapabilityState,
            Provider = proposal.ProviderId ?? provider.ProviderId,
            Model = proposal.Model,
            RegistryVersion = snapshot.RegistryVersion,
            Usage = proposal.Usage
        };
    }

    // Builds a NodeTypeId -> definition index for the validator, keeping the first definition when the
    // registry surfaces duplicate ids (it should not, but the validator dictionary must not throw).
    private static Dictionary<string, WorkflowNodeDefinition> BuildNodeIndex(
        IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        var index = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            index.TryAdd(node.NodeTypeId, node);
        }

        return index;
    }

    private static WorkflowGenerationResult Unsupported(string reason) => new()
    {
        Status = WorkflowGenerationStatus.Unsupported,
        Rationale = reason
    };

    private static string LabelFor(string providerId) => providerId switch
    {
        WorkflowGenerationConfiguration.LocalProviderId => "Local GIS model",
        WorkflowGenerationConfiguration.OpenAiProviderId => "GPT (OpenAI)",
        WorkflowGenerationConfiguration.AnthropicProviderId => "Claude (Anthropic)",
        WorkflowGenerationConfiguration.DeterministicProviderId => "Deterministic (fixtures)",
        _ => providerId
    };

    private static string? DescribeConfigured(string providerId, WorkflowGenerationProviderOptions? options)
    {
        if (string.Equals(providerId, WorkflowGenerationConfiguration.DeterministicProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return "Fixture replay (no model calls).";
        }

        return options is null ? null : $"{options.Model}";
    }
}
