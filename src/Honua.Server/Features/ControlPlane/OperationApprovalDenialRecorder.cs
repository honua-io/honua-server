// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal enum OperationApprovalDenialReason
{
    SelfApproval,
    EvidenceInvalid
}

/// <summary>Records a minimized, durable receipt for a terminating approval refusal.</summary>
internal static class OperationApprovalDenialRecorder
{
    internal const string UnavailableMessage = "The approval decision audit is unavailable.";

    public static async Task RecordAsync(
        IServiceScopeFactory scopeFactory,
        OperationProposal proposal,
        string actor,
        OperationApprovalDenialReason reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reasonCode = reason switch
        {
            OperationApprovalDenialReason.SelfApproval => "self_approval",
            OperationApprovalDenialReason.EvidenceInvalid => "evidence_invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var kind = Enum.IsDefined(proposal.Kind) ? proposal.Kind.ToString() : "Unknown";
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            if (!audit.IsPersisted) throw new ServiceUnavailableException(UnavailableMessage);
            var receipt = await audit.RecordAsync(new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = actor,
                ActorType = AuditActorType.UserId,
                ResourceType = "operation_proposal",
                ResourceId = proposal.ProposalId,
                Action = "operation.approval_denied",
                Outcome = AuditOutcome.Denied,
                CorrelationId = proposal.Audit.CorrelationId ?? proposal.ProposalId,
                Details = "{\"kind\":\"" + kind + "\",\"reason\":\"" + reasonCode + "\"}"
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(receipt)) throw new ServiceUnavailableException(UnavailableMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The global exception boundary logs the exception. Never retain a sink error as an inner exception.
            throw new ServiceUnavailableException(UnavailableMessage);
        }
    }
}
