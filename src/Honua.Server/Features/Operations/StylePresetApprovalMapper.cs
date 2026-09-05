// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal sealed class StylePresetApprovalMapper : IOperationApprovalRequestMapper
{
    public string OperationId => StylePresetOperation.OperationId;

    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
        {
            throw new ArgumentException("The mapper only accepts style.apply-preset requests.", nameof(request));
        }
        var payload = JsonSerializer.Serialize(new StylePresetApprovalPayload
        {
            Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal),
            DryRun = request.DryRun,
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
        }, StylePresetApprovalJsonContext.Default.StylePresetApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = payload,
            Plan = new OperationProposalPlan
            {
                Summary = $"{(request.DryRun ? "Preview" : "Apply")} style '{request.Parameters["styleId"]}' "
                    + $"on service '{request.Parameters["serviceId"]}', layer '{request.Parameters["layerId"]}'.",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = payload,
            },
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        var payload = JsonSerializer.Deserialize(request.Plan?.ExecutionPayload ?? request.ExecutionPayload
            ?? throw new InvalidOperationException("The style preset replay payload is unavailable."),
            StylePresetApprovalJsonContext.Default.StylePresetApprovalPayload)
            ?? throw new InvalidOperationException("The style preset replay payload is invalid.");
        return new OperationApprovalReplayMapping
        {
            Request = new OperationRequest { OperationId = OperationId, Parameters = payload.Parameters, DryRun = payload.DryRun },
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }
}

internal sealed record StylePresetApprovalPayload
{
    public Dictionary<string, string?> Parameters { get; init; } = new(StringComparer.Ordinal);
    public bool DryRun { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StylePresetApprovalPayload))]
internal sealed partial class StylePresetApprovalJsonContext : JsonSerializerContext;
