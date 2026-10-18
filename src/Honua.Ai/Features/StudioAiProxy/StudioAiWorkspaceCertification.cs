// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.AgentWorkspace.Abstractions;

namespace Honua.Ai.StudioAiProxy;

/// <summary>Allows scoped agent signing only for the exact durable turn authorized by the server.</summary>
internal static class StudioAiWorkspaceCertification
{
    internal static async Task<bool> AuthorizeAsync(
        IAgentWorkspaceTurnAuthorizer? authorizer,
        ClaimsPrincipal principal,
        string workspaceId,
        string turnId,
        string tenantId,
        StudioAiTranscriptCertification certification,
        CancellationToken cancellationToken)
    {
        if (authorizer is null || string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(turnId))
        {
            return false;
        }

        var binding = await authorizer.AuthorizeAsync(principal, workspaceId, turnId, cancellationToken).ConfigureAwait(false);
        return binding is not null
            && !string.IsNullOrWhiteSpace(binding.ReleaseId)
            && string.Equals(binding.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && string.Equals(binding.TurnId, turnId, StringComparison.Ordinal)
            && string.Equals(binding.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(certification.CandidateId, binding.TargetId, StringComparison.Ordinal)
            && string.Equals(certification.EndpointIdentity, binding.TargetId, StringComparison.Ordinal)
            && string.Equals(certification.ReleaseId, binding.ReleaseId, StringComparison.Ordinal)
            && string.Equals(certification.ActionId, binding.TurnId, StringComparison.Ordinal)
            && string.Equals(certification.RunNonce, binding.TurnId, StringComparison.Ordinal);
    }
}
