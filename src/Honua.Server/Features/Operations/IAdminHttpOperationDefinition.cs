// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal interface IAdminHttpOperationDefinition
{
    string OperationId { get; }
    string Title { get; }
    HttpMethod Method { get; }
    string Path { get; }
    string OpenApiOperationId { get; }
    OperationSideEffectClass SideEffect { get; }
    OperationBlastRadiusClass BlastRadius { get; }
    bool SupportsDryRun { get; }
    string? DryRunPath { get; }
    HttpMethod? DryRunMethod { get; }
    string? ContentType { get; }
    OperationApprovalModel? ApprovalModel { get; }
    OperationClass OperationClass { get; }
}

internal static class AdminOperationEndpointExtensions
{
    public static RouteHandlerBuilder WithAdminOperation(
        this RouteHandlerBuilder endpoint, IAdminHttpOperationDefinition definition)
        => endpoint.WithName(definition.OpenApiOperationId).WithMetadata(definition);
}

internal sealed record AdminOperationApprovalMapping(
    IOperationDescriptor Descriptor, OperationRequest Request, OperationPolicyContext PolicyContext)
{
    public required PolicyDecision Decision { get; init; }
}

internal static class AdminOperationApprovalRequest
{
    public static OperationGatewayRequest Create(
        IAdminHttpOperationDefinition definition, AdminOperationApprovalMapping mapping, string payload)
        => new()
        {
            OperationInstanceId = mapping.PolicyContext.OperationInstanceId,
            OperationId = definition.OperationId,
            Kind = definition.OperationClass,
            RequestedBy = mapping.PolicyContext.PrincipalId,
            Reason = mapping.Decision.Reason,
            CorrelationId = mapping.PolicyContext.CorrelationId,
            ExecutionPayload = payload,
            Plan = AdminOperationReview.Create(mapping.Descriptor, mapping.Request, mapping.PolicyContext,
                definition.Method.Method, definition.Path,
                definition.SideEffect == OperationSideEffectClass.DestroysState ? ProposalRiskLevel.High : ProposalRiskLevel.Medium,
                payload)
        };
}
