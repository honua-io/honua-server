// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.ControlPlane;

/// <summary>Fail-closed approval-time revalidation for model-originated proposals.</summary>
internal sealed class OperationProposalEvidenceValidator(
    ProposalEvidenceVerifier verifier,
    IServiceProvider services,
    IGuardrailLadder ladder,
    IAdminApiKeyStore apiKeys,
    TimeProvider timeProvider) : IOperationProposalEvidenceValidator
{
    public async Task ValidateApprovalAsync(
        OperationProposal proposal,
        OperationProposalApprovalContext approval,
        CancellationToken cancellationToken = default)
    {
        var evidence = proposal.Evidence
            ?? throw new InvalidOperationException("The proposal has no evidence seal.");
        if (string.Equals(proposal.RequestedBy, approval.ApprovedBy, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Separation of duties: the requester cannot approve this proposal.");
        }

        if (!string.Equals(evidence.TenantId, approval.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The proposal tenant does not match the approval tenant.");
        }

        if (evidence.ExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("The proposal evidence has expired.");
        }

        if (!TryParseApiKeyActor(proposal.RequestedBy, out var proposerKeyId))
        {
            throw new InvalidOperationException(
                "The proposer authority cannot be revalidated at approval time.");
        }

        var proposer = await apiKeys.GetAsync(proposerKeyId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (proposer is null || proposer.RevokedAt is not null || proposer.ExpiresAt <= now)
        {
            throw new InvalidOperationException("The proposer is no longer authorized.");
        }

        var proposerIdentity = new ClaimsIdentity(
            proposer.Permissions.Select(permission =>
                new Claim(AdminApiKeyPermission.PermissionClaimType, permission)),
            authenticationType: "proposal-evidence-revalidation",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        proposerIdentity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        if (!AdminApiKeyPermission.IsAuthorized(new ClaimsPrincipal(proposerIdentity), HttpMethods.Post)
            || !string.Equals(evidence.AuthorizationDecision, "admin-policy-authorized", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The proposer no longer has the authority sealed into the proposal.");
        }

        var tool = services.GetServices<IMcpTool>().SingleOrDefault(candidate =>
            string.Equals(candidate.Name, evidence.ToolName, StringComparison.Ordinal));
        if (tool is not IEvidenceBoundProposalTool proposalTool
            || !string.Equals(proposalTool.OperationId, evidence.OperationId, StringComparison.Ordinal)
            || !string.Equals(proposal.OperationId, evidence.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The proposal operation descriptor is no longer supported.");
        }

        byte[] requestBytes;
        try { requestBytes = Convert.FromBase64String(evidence.CanonicalRequest); }
        catch (FormatException) { throw new InvalidOperationException("The sealed proposal request is invalid."); }
        if (!FixedTimeHexEquals(evidence.RequestDigest, SHA256.HashData(requestBytes)))
        {
            throw new InvalidOperationException("The sealed proposal request digest does not match.");
        }

        byte[] payloadBytes;
        try { payloadBytes = Convert.FromBase64String(evidence.CanonicalPayload); }
        catch (FormatException) { throw new InvalidOperationException("The sealed proposal payload is invalid."); }
        if (!FixedTimeHexEquals(evidence.PayloadDigest, SHA256.HashData(payloadBytes)))
        {
            throw new InvalidOperationException("The sealed proposal payload digest does not match.");
        }

        if (string.IsNullOrWhiteSpace(proposal.Plan.ExecutionPayload))
        {
            throw new InvalidOperationException("The durable proposal execution payload is missing.");
        }

        byte[] durablePayload;
        try
        {
            durablePayload = Honua.Ai.StudioAiProxy.StudioAiTranscriptSigner.Canonicalize(
                System.Text.Encoding.UTF8.GetBytes(proposal.Plan.ExecutionPayload));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The durable proposal execution payload is invalid.");
        }
        if (!CryptographicOperations.FixedTimeEquals(durablePayload, payloadBytes))
        {
            throw new InvalidOperationException("The durable proposal execution payload does not match its evidence seal.");
        }

        using var arguments = JsonDocument.Parse(requestBytes);
        var signed = new StudioAiSignedTranscript
        {
            KeyId = evidence.TranscriptKeyId,
            CanonicalTranscript = evidence.CanonicalTranscript,
            TranscriptDigest = evidence.TranscriptDigest,
            Signature = evidence.TranscriptSignature,
        };
        var signedJson = JsonSerializer.Serialize(
            signed,
            Honua.Ai.StudioAiProxy.StudioAiProxyJsonContext.Default.StudioAiSignedTranscript);
        using var metadata = JsonDocument.Parse(
            $$"""{"{{ProposalEvidenceVerifier.MetaProperty}}":{{signedJson}}}""");
        using var callId = JsonDocument.Parse(evidence.McpCallId);
        var verified = await verifier.VerifyAsync(
            tool,
            arguments.RootElement,
            metadata.RootElement,
            approval.TenantId,
            evidence.McpSessionId,
            callId.RootElement,
            cancellationToken).ConfigureAwait(false);
        if (verified with
        {
            PolicyRevision = evidence.PolicyRevision,
            AuthorizationDecision = evidence.AuthorizationDecision,
            PayloadDigest = evidence.PayloadDigest,
            CanonicalPayload = evidence.CanonicalPayload,
        } != evidence)
        {
            throw new InvalidOperationException("The durable proposal evidence seal does not match its signed transcript.");
        }

        var baseDecision = ladder.Resolve(proposal.Kind);
        var currentDecision = baseDecision.Tier == GuardrailTier.RequiresApproval
            ? baseDecision
            : new GuardrailDecision(
                GuardrailTier.RequiresApproval,
                proposal.Kind,
                baseDecision.Edition,
                "upstream-gate-requires-approval");
        if (!string.Equals(
                evidence.PolicyRevision,
                OperationGateway.ComputePolicyRevision(currentDecision),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The governing operation policy revision has changed.");
        }
    }

    private static bool TryParseApiKeyActor(string? actor, out Guid id)
    {
        id = default;
        const string marker = ":api-key:";
        var markerIndex = actor?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return markerIndex > 0 && Guid.TryParse(actor![(markerIndex + marker.Length)..], out id);
    }

    private static bool FixedTimeHexEquals(string actual, byte[] expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), expected); }
        catch (FormatException) { return false; }
    }
}
