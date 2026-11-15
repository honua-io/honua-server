// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Geoprocessing;

internal sealed class StagedImportApprovalMapper : IOperationApprovalRequestMapper
{
    public string OperationId => StagedImportOperation.ImportOperationId;

    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
        {
            throw new ArgumentException("The mapper only accepts dataset.stage.import requests.", nameof(request));
        }

        var pin = StagedImportPin.Read(request);
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var table = StagedImportOperation.ParseCommand(request, tenantId, actorId).Table;
        var payload = new StagedImportApprovalPayload
        {
            Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal),
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
        };
        var serialized = JsonSerializer.Serialize(payload, StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = $"Import {pin.Preview.FeatureCount} feature(s) from job '{payload.Parameters["jobId"]}' "
                    + $"into new server-owned staging table '{payload.Parameters["schema"]}.{table}'. "
                    + $"Content SHA-256 {pin.Preview.ContentSha256}; approval expires {pin.ExpiresAt:O}.",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        // Replay reads only the plan payload sealed by the approved plan hash.
        var payload = JsonSerializer.Deserialize(request.Plan?.ExecutionPayload
            ?? throw new InvalidOperationException("The staged import replay payload is unavailable."),
            StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload)
            ?? throw new InvalidOperationException("The staged import replay payload is invalid.");
        var replay = new OperationRequest { OperationId = OperationId, Parameters = payload.Parameters };
        _ = StagedImportPin.Read(replay);
        return new OperationApprovalReplayMapping
        {
            Request = replay,
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }
}

internal sealed record StagedImportApprovalPayload
{
    public Dictionary<string, string?> Parameters { get; init; } = new(StringComparer.Ordinal);
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StagedImportApprovalPayload))]
internal sealed partial class StagedImportApprovalJsonContext : JsonSerializerContext;
