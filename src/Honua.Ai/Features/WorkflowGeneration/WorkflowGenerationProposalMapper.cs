// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.WorkflowGeneration.Models;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Maps the model/fixture structured-output shape into the neutral
/// <see cref="WorkflowGenerationProposal"/> returned to the host.
/// </summary>
internal static class WorkflowGenerationProposalMapper
{
    /// <summary>
    /// Projects a model proposal into a provider proposal, stamping provider/model/usage.
    /// </summary>
    public static WorkflowGenerationProposal ToProposal(
        WorkflowGenerationModelProposal model,
        string providerId,
        string modelId,
        WorkflowGenerationUsage? usage)
    {
        var status = ParseStatus(model.Status);

        return new WorkflowGenerationProposal
        {
            Status = status,
            Graph = status == WorkflowGenerationStatus.Generated ? model.Graph : null,
            Rationale = model.Rationale,
            Clarifications = model.Clarifications,
            UnmappedRequests = model.UnmappedRequests,
            CapabilityState = model.CapabilityState,
            ProviderId = providerId,
            Model = modelId,
            Usage = usage
        };
    }

    /// <summary>
    /// Parses a status string into the enum. Unknown values fall back to error, which the
    /// host surfaces as a retryable transport/provider error.
    /// </summary>
    public static WorkflowGenerationStatus ParseStatus(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "generated" => WorkflowGenerationStatus.Generated,
            "needs-clarification" or "needsclarification" or "clarification_required" => WorkflowGenerationStatus.NeedsClarification,
            "unsupported" => WorkflowGenerationStatus.Unsupported,
            "refused" or "rejected" => WorkflowGenerationStatus.Refused,
            _ => WorkflowGenerationStatus.Error
        };
}
