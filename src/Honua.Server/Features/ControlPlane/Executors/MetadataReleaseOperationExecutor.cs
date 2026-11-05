// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.ControlPlane.Executors;

// This executor accepts only the create action; rollback is not a new proposal, and coordinated releases already own their approval gates.
internal sealed class MetadataReleaseOperationExecutor(MetadataReleaseControlService controlService) : IOperationExecutor
{
    public const string CreateAction = "create";

    public OperationClass OperationClass => OperationClass.MetadataRelease;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = MetadataReleaseExecutionPayload.ParseAndValidate(request.ExecutionPayload);
        if (payload == null)
        {
            return Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = "Metadata release operation",
                BlockingReasons = [error ?? "Metadata release execution payload is missing or malformed."],
                RiskLevel = ProposalRiskLevel.High,
            });
        }

        var diff = new List<string>
        {
            $"package: {payload.PackageId}",
            $"environment: {payload.TargetEnvironment}",
            $"resource: {payload.ResourceSemanticId}",
            payload.NewFieldType is null
                ? $"field: {payload.NewFieldName}"
                : $"field: {payload.NewFieldName} ({payload.NewFieldType})",
        };

        return Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
        {
            Summary = $"Create metadata release for {payload.PackageId} ({payload.ResourceSemanticId}.{payload.NewFieldName})",
            Diff = diff,
            RiskLevel = ProposalRiskLevel.Medium,
            ExecutionPayload = request.ExecutionPayload,
        });
    }

    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = MetadataReleaseExecutionPayload.ParseAndValidate(executionPayload);
        if (payload == null)
        {
            throw new InvalidOperationException(error ?? "Metadata release execution payload is missing or malformed.");
        }

        var root = RootProvisioningOperationIds.PreferCanonical(
            request.RootProvisioningOperationId,
            payload.RootProvisioningOperationId);
        var operation = await controlService.CreateAsync(
                payload.ToExecutionPlan(),
                request.RequestedBy,
                request.Reason,
                request.IdempotencyKey,
                request.CorrelationId,
                root,
                cancellationToken)
            .ConfigureAwait(false);

        return operation.OperationId;
    }

    public async Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await controlService.GetAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
        return operation is null ? null : new OperationBackendStatus
        {
            State = operation.Status switch
            {
                WorkflowOperationStatus.Succeeded => OperationBackendState.Succeeded,
                WorkflowOperationStatus.Failed or WorkflowOperationStatus.RolledBack => OperationBackendState.Failed,
                WorkflowOperationStatus.ManualInterventionRequired => OperationBackendState.Indeterminate,
                WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Planned or WorkflowOperationStatus.AwaitingApproval
                    => OperationBackendState.Queued,
                _ => OperationBackendState.Running,
            },
            Reason = operation.Status is WorkflowOperationStatus.Failed or WorkflowOperationStatus.RolledBack
                ? $"Backend workflow reached {operation.Status}."
                : null,
        };
    }
}

internal sealed record MetadataReleaseExecutionPayload
{
    public string Action { get; init; } = MetadataReleaseOperationExecutor.CreateAction;

    public required string PackageId { get; init; }

    public required string TargetEnvironment { get; init; }

    public required string ResourceSemanticId { get; init; }

    public required string NewFieldName { get; init; }

    public string? NewFieldType { get; init; }

    public string? DataPopulateWorkloadId { get; init; }

    // The release is rejected before mutation unless every populated field is the new nullable field.
    public IReadOnlyList<string>? DataPopulateFields { get; init; }

    public string? ScriptId { get; init; }

    public string? RootProvisioningOperationId { get; init; }

    public static (MetadataReleaseExecutionPayload? Payload, string? Error) ParseAndValidate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, "Metadata release execution payload is required.");
        }

        MetadataReleaseExecutionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, MetadataReleaseExecutionPayloadJsonContext.Default.MetadataReleaseExecutionPayload);
        }
        catch (JsonException)
        {
            return (null, "Metadata release execution payload is not valid JSON.");
        }

        if (payload == null)
        {
            return (null, "Metadata release execution payload is missing or malformed.");
        }

        if (!string.Equals(payload.Action, MetadataReleaseOperationExecutor.CreateAction, StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"Unsupported metadata release action '{payload.Action}'. Only " +
                $"'{MetadataReleaseOperationExecutor.CreateAction}' is supported through the gateway; rollback and " +
                "coordinated-release actions stay endpoint/cockpit-driven.");
        }

        if (string.IsNullOrWhiteSpace(payload.PackageId) ||
            string.IsNullOrWhiteSpace(payload.TargetEnvironment) ||
            string.IsNullOrWhiteSpace(payload.ResourceSemanticId) ||
            string.IsNullOrWhiteSpace(payload.NewFieldName))
        {
            return (null, "packageId, targetEnvironment, resourceSemanticId, and newFieldName are required.");
        }

        return (payload, null);
    }

    public MetadataReleaseExecutionPlan ToExecutionPlan()
    {
        var scriptId = string.IsNullOrWhiteSpace(ScriptId) ? $"add-{NewFieldName}" : ScriptId!;
        return new MetadataReleaseExecutionPlan
        {
            PackageId = PackageId,
            TargetEnvironment = TargetEnvironment,
            ResourceSemanticId = ResourceSemanticId,
            NewFieldName = NewFieldName,
            DataPopulateWorkloadId = DataPopulateWorkloadId,
            DataPopulateFields = DataPopulateFields ?? Array.Empty<string>(),
            Script = new MetadataReleaseScript
            {
                ScriptId = scriptId,
                Reversible = true,
                ForwardOperations =
                [
                    new MetadataReleaseScriptOperation
                    {
                        Kind = MetadataReleaseScriptOperationKind.AddColumn,
                        ResourceSemanticId = ResourceSemanticId,
                        FieldName = NewFieldName,
                        FieldType = NewFieldType,
                        Nullable = true,
                    }
                ]
            }
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MetadataReleaseExecutionPayload))]
internal sealed partial class MetadataReleaseExecutionPayloadJsonContext : JsonSerializerContext
{
}
