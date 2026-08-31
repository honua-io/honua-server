// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Fail-closed adapter from typed operation policy decisions to the durable proposal gateway.
/// </summary>
internal sealed partial class AdminOperationApprovalBridge(
    IServiceProvider services,
    IEnumerable<IOperationApprovalRequestMapper> requestMappers,
    ILogger<AdminOperationApprovalBridge> logger) : IOperationApprovalBridge
{
    private readonly Dictionary<string, IOperationApprovalRequestMapper> _requestMappers =
        requestMappers.ToDictionary(mapper => mapper.OperationId, StringComparer.Ordinal);

    public async Task<OperationApprovalBridgeResult> CreateProposalAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);

        _requestMappers.TryGetValue(descriptor.OperationId, out var mapper);
        if (request.GatewayRequest is null && mapper is null)
        {
            return Failure(
                $"Approval is required, but operation '{descriptor.OperationId}' has no durable replay mapper.");
        }

        var gateway = services.GetService<IOperationGateway>();
        if (gateway is null)
        {
            return Failure("Approval is required, but the durable proposal gateway is unavailable.");
        }

        if (string.IsNullOrWhiteSpace(context.OperationInstanceId) ||
            string.IsNullOrWhiteSpace(context.CorrelationId))
        {
            return Failure("Approval is required, but the canonical operation identity is incomplete.");
        }

        if (context.ScopeGoverned && context.RecognizedScopes.Count == 0)
        {
            return Failure("Approval is required, but the OAuth proposer has no recognized scope authority.");
        }

        try
        {
            var gatewayRequest = (mapper?.Map(descriptor, request, context, decision)
                ?? request.GatewayRequest!) with
            {
                OperationId = descriptor.OperationId,
                OperationInstanceId = context.OperationInstanceId,
                CorrelationId = context.CorrelationId,
                ScopeGoverned = context.ScopeGoverned,
                RecognizedScopes = context.RecognizedScopes,
            };
            var guardrails = services.GetService<IGuardrailLadder>();
            if (guardrails is null || guardrails.Resolve(gatewayRequest.Kind).Tier == GuardrailTier.Blocked)
            {
                return Failure("The control-plane guardrail blocks this operation; approval cannot grant authority.");
            }

            var result = await gateway
                .CreateApprovalProposalAsync(context.OperationInstanceId, gatewayRequest, cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome != OperationGatewayOutcome.ProposalCreated ||
                string.IsNullOrWhiteSpace(result.ProposalId) ||
                string.IsNullOrWhiteSpace(result.AuditId))
            {
                return Failure(result.Message
                    ?? "Approval is required, but durable proposal or audit persistence failed.");
            }

            return new OperationApprovalBridgeResult
            {
                IsDurable = true,
                ProposalId = result.ProposalId,
                AuditId = result.AuditId,
                Reason = result.Message,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.PersistenceFailed(logger, descriptor.OperationId, ex);
            return Failure("Approval is required, but durable proposal infrastructure failed.");
        }
    }

    private static OperationApprovalBridgeResult Failure(string reason) => new()
    {
        IsDurable = false,
        Reason = reason,
    };

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7421,
            Level = LogLevel.Error,
            Message = "Failed to persist approval for typed operation '{OperationId}'.")]
        public static partial void PersistenceFailed(
            ILogger logger,
            string operationId,
            Exception exception);
    }
}
