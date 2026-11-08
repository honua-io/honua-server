// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

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

        if (payload.Rollback && !string.IsNullOrWhiteSpace(payload.BackupId))
        {
            diff.Add($"backup: {payload.BackupId}");
        }

        var dryRun = new List<string>();
        if (planResult.Plan.RequiresOutOfBandMigrations) dryRun.Add("requires out-of-band migrations");
        if (payload.Rollback && !string.IsNullOrWhiteSpace(payload.BackupId))
            dryRun.Add($"Verify backup {payload.BackupId}; restore it before starting the prior revision when required by reader compatibility.");

        return new OperationProposalPlan
        {
            Summary = $"Deploy {payload.DesiredRevision} to {planResult.Target.TargetId}",
            Diff = diff,
            DryRun = dryRun,
            RiskLevel = planResult.Plan.RequiresOutOfBandMigrations ? ProposalRiskLevel.High : ProposalRiskLevel.Medium,
            BlockingReasons = planResult.Plan.BlockingReasons,
            Warnings = planResult.Plan.Warnings,
            ExecutionPayload = request.ExecutionPayload,
        };
    }

    public Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(request, executionPayload, null, cancellationToken);

    internal Task<string?> ExecuteRestoreAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        string restoreOwnerId,
        CancellationToken cancellationToken)
        => ExecuteCoreAsync(request, executionPayload, restoreOwnerId, cancellationToken);

    private async Task<string?> ExecuteCoreAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        string? restoreOwnerId,
        CancellationToken cancellationToken)
    {
        var payload = DeployExecutionPayload.Parse(executionPayload);
        if (payload == null)
        {
            throw new InvalidOperationException("Deploy execution payload is missing or malformed.");
        }

        if (!string.IsNullOrWhiteSpace(request.ApprovedBy) || !string.IsNullOrWhiteSpace(request.ApprovedProposalId))
        {
            return (await deployWorkflowService.CreateApprovedWithBackupAsync(request, payload, restoreOwnerId,
                cancellationToken).ConfigureAwait(false))?.OperationId;
        }

        var record = await deployWorkflowService.CreateWithBackupAsync(
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
                preMigrationBackupId: payload.Rollback ? payload.BackupId : null,
                restoreOwnerId,
                request.ApprovedBy,
                cancellationToken)
            .ConfigureAwait(false);

        return record?.OperationId;
    }

    public async Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await deployWorkflowService.GetAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
        return operation is null ? null : BackendStatus(operation.Status);
    }

    internal Task<WorkflowOperationRecord?> GetWorkflowAsync(string operationId, CancellationToken cancellationToken)
        => deployWorkflowService.GetAsync(operationId, cancellationToken);

    internal Task RecordInstalledRevisionAsync(string targetId, string revision, WorkflowOperationRecord convergence, CancellationToken cancellationToken)
        => deployWorkflowService.RecordInstalledRevisionAsync(targetId, revision, convergence, cancellationToken);

    private static OperationBackendStatus BackendStatus(WorkflowOperationStatus status) => new()
    {
        State = status switch
        {
            WorkflowOperationStatus.Succeeded => OperationBackendState.Succeeded,
            WorkflowOperationStatus.Failed => OperationBackendState.Failed,
            WorkflowOperationStatus.RolledBack => OperationBackendState.Failed,
            WorkflowOperationStatus.ManualInterventionRequired => OperationBackendState.Indeterminate,
            WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Planned or WorkflowOperationStatus.AwaitingApproval
                => OperationBackendState.Queued,
            _ => OperationBackendState.Running,
        },
        Reason = status is WorkflowOperationStatus.Failed or WorkflowOperationStatus.RolledBack
            ? $"Backend workflow reached {status}."
            : null,
    };
}

internal sealed record DeployExecutionPayload
{
    public required string TargetId { get; init; }

    public required string DesiredRevision { get; init; }

    public string? CurrentRevision { get; init; }

    public OperationPriority Priority { get; init; } = OperationPriority.Normal;

    public Dictionary<string, string>? ParameterOverrides { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Rollback { get; init; }

    // The backup id is ignored unless restore is requested, and it is never read from parameter overrides.
    public string? BackupId { get; init; }

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
