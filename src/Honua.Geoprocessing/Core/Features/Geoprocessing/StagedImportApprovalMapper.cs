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

internal sealed class StagedImportApprovalMapper(string operationId = StagedImportOperation.ImportOperationId) : IOperationApprovalRequestMapper
{
    public string OperationId => operationId;

    public OperationGatewayRequest Map(OperationApprovalRequestContext input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Descriptor.OperationId != OperationId || input.Request.OperationId != OperationId)
        {
            throw new ArgumentException($"The mapper only accepts {OperationId} requests.", nameof(input));
        }

        return OperationId == StagedImportOperation.DiscardOperationId
            ? MapDiscard(input.Request, input.Context, input.Decision)
            : MapImport(input.Request, input.Context, input.Decision);
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        // Replay reads only the plan payload sealed by the approved plan hash.
        var payload = JsonSerializer.Deserialize(request.Plan?.ExecutionPayload
            ?? throw new InvalidOperationException("The staged import replay payload is unavailable."),
            StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload)
            ?? throw new InvalidOperationException("The staged import replay payload is invalid.");
        var replay = new OperationRequest { OperationId = OperationId, Parameters = payload.Parameters };
        if (OperationId == StagedImportOperation.DiscardOperationId)
        {
            _ = StagedImportOperation.Required(replay, "intentId");
        }
        else
        {
            _ = StagedImportPin.Read(replay);
        }

        return new OperationApprovalReplayMapping
        {
            Request = replay,
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }

    private OperationGatewayRequest MapImport(OperationRequest request, OperationPolicyContext context, PolicyDecision decision)
    {
        var pin = StagedImportPin.Read(request);
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var table = StagedImportOperation.ParseCommand(request, tenantId, actorId).Table;
        var payload = new StagedImportApprovalPayload
        {
            Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal),
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
        };
        var summary = $"Import {pin.Preview.FeatureCount} feature(s) from job '{payload.Parameters["jobId"]}' "
            + $"into new server-owned staging table '{payload.Parameters["schema"]}.{table}'. "
            + $"Content SHA-256 {pin.Preview.ContentSha256}; approval expires {pin.ExpiresAt:O}.";
        return BuildGatewayRequest(payload, context, (decision.Reason, summary));
    }

    private OperationGatewayRequest MapDiscard(OperationRequest request, OperationPolicyContext context, PolicyDecision decision)
    {
        if (request.Parameters.ContainsKey("table") || request.Parameters.ContainsKey("schema"))
        {
            throw new GeoprocessingValidationException(
                "The staging table is resolved from the committed import receipt; do not supply 'table' or 'schema'.");
        }

        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var intentId = StagedImportOperation.Required(request, "intentId");
        var table = StagedImportOperation.DestinationTable(tenantId, actorId, intentId);
        var payload = new StagedImportApprovalPayload
        {
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = intentId },
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
        };
        var summary = $"Discard staged import intent '{intentId}' and drop its server-owned staging table '{table}' if unreferenced.";
        return BuildGatewayRequest(payload, context, (decision.Reason, summary));
    }

    private OperationGatewayRequest BuildGatewayRequest(
        StagedImportApprovalPayload payload,
        OperationPolicyContext context,
        (string? Reason, string Summary) plan)
    {
        var serialized = JsonSerializer.Serialize(payload, StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = plan.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = plan.Summary,
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
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
