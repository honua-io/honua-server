// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Gateway executor that adapts the existing two-phase deploy workflow
/// (<see cref="DeployWorkflowService"/>) to the shared operation gateway. This is
/// the first adapter over <see cref="IOperationGateway"/>; the durable record,
/// plan production, and backend submission stay in the deploy service (#1693).
/// </summary>
internal sealed class DeployOperationExecutor(DeployWorkflowService deployWorkflowService) : IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.Deploy;

    public async Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = DeployExecutionPayload.Parse(request.ExecutionPayload);
        if (payload == null)
        {
            return new OperationProposalPlan
            {
                Summary = "Deploy operation",
                BlockingReasons = ["Deploy execution payload is missing or malformed."],
                RiskLevel = ProposalRiskLevel.High,
            };
        }

        var planResult = await deployWorkflowService.PlanAsync(
                payload.TargetId,
                payload.DesiredRevision,
                payload.CurrentRevision,
                payload.ParameterOverrides,
                principal: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (planResult == null)
        {
            return new OperationProposalPlan
            {
                Summary = $"Deploy {payload.DesiredRevision} to {payload.TargetId}",
                BlockingReasons = [$"Deploy target '{payload.TargetId}' was not found."],
                RiskLevel = ProposalRiskLevel.High,
                ExecutionPayload = request.ExecutionPayload,
            };
        }

        var diff = new List<string>
        {
            $"target: {planResult.Target.TargetId} ({planResult.Target.Environment})",
            $"revision: {planResult.Spec.CurrentRevision ?? "(unknown)"} -> {planResult.Spec.DesiredRevision}",
        };

        return new OperationProposalPlan
        {
            Summary = $"Deploy {payload.DesiredRevision} to {planResult.Target.TargetId}",
            Diff = diff,
            DryRun = planResult.Plan.RequiresOutOfBandMigrations
                ? ["requires out-of-band migrations"]
                : Array.Empty<string>(),
            RiskLevel = planResult.Plan.RequiresOutOfBandMigrations ? ProposalRiskLevel.High : ProposalRiskLevel.Medium,
            BlockingReasons = planResult.Plan.BlockingReasons,
            Warnings = planResult.Plan.Warnings,
            ExecutionPayload = request.ExecutionPayload,
        };
    }

    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        var payload = DeployExecutionPayload.Parse(executionPayload);
        if (payload == null)
        {
            throw new InvalidOperationException("Deploy execution payload is missing or malformed.");
        }

        var record = await deployWorkflowService.CreateAsync(
                payload.TargetId,
                payload.DesiredRevision,
                payload.CurrentRevision,
                request.RequestedBy,
                request.Reason,
                request.IdempotencyKey,
                request.CorrelationId,
                payload.Priority,
                submitImmediately: true,
                payload.ParameterOverrides,
                principal: null,
                cancellationToken)
            .ConfigureAwait(false);

        return record?.OperationId;
    }
}

/// <summary>
/// Class-specific execution payload for deploy operations carried through the gateway.
/// </summary>
internal sealed record DeployExecutionPayload
{
    /// <summary>Stable deploy target identifier.</summary>
    public required string TargetId { get; init; }

    /// <summary>Desired revision to deploy.</summary>
    public required string DesiredRevision { get; init; }

    /// <summary>Currently observed revision, when known.</summary>
    public string? CurrentRevision { get; init; }

    /// <summary>Relative operator priority.</summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    /// <summary>Backend parameter overrides.</summary>
    public Dictionary<string, string>? ParameterOverrides { get; init; }

    /// <summary>Parses a serialized payload, returning null when missing/malformed.</summary>
    public static DeployExecutionPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, DeployExecutionPayloadJsonContext.Default.DeployExecutionPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes this payload to JSON for storage on the proposal.</summary>
    public string Serialize()
        => JsonSerializer.Serialize(this, DeployExecutionPayloadJsonContext.Default.DeployExecutionPayload);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeployExecutionPayload))]
internal sealed partial class DeployExecutionPayloadJsonContext : JsonSerializerContext
{
}
