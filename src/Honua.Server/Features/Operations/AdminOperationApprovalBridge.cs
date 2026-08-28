// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

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

        if (!_requestMappers.TryGetValue(descriptor.OperationId, out var mapper))
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

        try
        {
            var gatewayRequest = mapper.Map(descriptor, request, context, decision) with
            {
                OperationInstanceId = context.OperationInstanceId,
                CorrelationId = context.CorrelationId,
            };
            var result = await gateway
                .CreateApprovalProposalAsync(gatewayRequest, cancellationToken)
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

/// <summary>
/// Explicit compatibility mapper for a typed operation whose sealed request can be replayed by
/// the current durable proposal runtime. Slice 2 replaces these adapters with the unified runtime.
/// </summary>
internal interface IOperationApprovalRequestMapper
{
    /// <summary>
    /// Typed descriptor identity this mapper supports.
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Maps a bounded typed request into the current durable gateway request.
    /// </summary>
    OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision);
}
