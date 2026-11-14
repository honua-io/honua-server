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
        return StagedApprovalRequests.Build(new StagedApprovalInput
        {
            OperationId = OperationId,
            Request = request,
            Context = context,
            Decision = decision,
            Summary = $"Import {pin.Preview.FeatureCount} feature(s) from job '{StagedImportOperation.Required(request, "jobId")}' "
                + $"into new server-owned staging table '{StagedImportOperation.Required(request, "schema")}.{table}'. "
                + $"Content SHA-256 {pin.Preview.ContentSha256}; approval expires {pin.ExpiresAt:O}.",
        });
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        var replay = StagedApprovalRequests.ReadReplay(OperationId, request);
        _ = StagedImportPin.Read(replay.Request);
        return replay;
    }
}

// Shared by the staged import and discard mappers: one sealed payload shape and one replay reader.
internal sealed record StagedApprovalInput
{
    public required string OperationId { get; init; }
    public required OperationRequest Request { get; init; }
    public required OperationPolicyContext Context { get; init; }
    public required PolicyDecision Decision { get; init; }
    public required string Summary { get; init; }
}

internal static class StagedApprovalRequests
{
    public static OperationGatewayRequest Build(StagedApprovalInput input)
    {
        var payload = new StagedImportApprovalPayload
        {
            Parameters = new Dictionary<string, string?>(input.Request.Parameters, StringComparer.Ordinal),
            TenantId = input.Context.TenantId,
            SchemaName = input.Context.SchemaName,
        };
        var serialized = JsonSerializer.Serialize(payload, StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = input.Context.OperationInstanceId,
            OperationId = input.OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = input.Context.PrincipalId,
            Reason = input.Decision.Reason,
            CorrelationId = input.Context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = input.Summary,
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
        };
    }

    // Replay reads only the plan payload sealed by the approved plan hash.
    public static OperationApprovalReplayMapping ReadReplay(string operationId, OperationGatewayRequest request)
    {
        var payload = JsonSerializer.Deserialize(request.Plan?.ExecutionPayload
            ?? throw new InvalidOperationException("The staged replay payload is unavailable."),
            StagedImportApprovalJsonContext.Default.StagedImportApprovalPayload)
            ?? throw new InvalidOperationException("The staged replay payload is invalid.");
        return new OperationApprovalReplayMapping
        {
            Request = new OperationRequest { OperationId = operationId, Parameters = payload.Parameters },
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
