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
/// Gateway executor for metadata-release proposals. Adapts
/// <see cref="MetadataReleaseControlService.CreateAsync"/> ONLY (#2563) — the same additive
/// create path <see cref="Honua.Server.Features.Admin.MetadataReleaseControlEndpoints"/> builds
/// inline today. The execution payload therefore carries an explicit <c>action</c> discriminator
/// with <see cref="CreateAction"/> as the ONLY supported value.
/// </summary>
/// <remarks>
/// Two related surfaces are deliberately NOT adapted here, by design:
/// <list type="bullet">
/// <item>
/// Metadata-release rollback is an action on an EXISTING release operation id (advance an
/// already-submitted lifecycle backward), not a new proposal this create-only payload shape can
/// express. It stays endpoint/cockpit-driven.
/// </item>
/// <item>
/// <see cref="CoordinatedReleaseControlService"/> has its own
/// approval-gate model (an operator-facing coordinated-release request lane); routing it through
/// this gateway would stack a second human-approval layer on top of gateway proposals. It stays
/// endpoint-driven.
/// </item>
/// </list>
/// </remarks>
internal sealed class MetadataReleaseOperationExecutor(MetadataReleaseControlService controlService) : IOperationExecutor
{
    /// <summary>The only execution-payload action this executor supports.</summary>
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

        var operation = await controlService.CreateAsync(
                payload.ToExecutionPlan(),
                request.RequestedBy,
                request.Reason,
                request.IdempotencyKey,
                request.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);

        return operation.OperationId;
    }
}

/// <summary>
/// Class-specific execution payload for metadata-release operations carried through the gateway.
/// Mirrors <see cref="Honua.Server.Features.Admin.Models.CreateMetadataReleaseOperationRequest"/>
/// plus the explicit <see cref="Action"/> discriminator (#2563).
/// </summary>
internal sealed record MetadataReleaseExecutionPayload
{
    /// <summary>
    /// Action discriminator. Only <see cref="MetadataReleaseOperationExecutor.CreateAction"/> is
    /// supported; any other value (including a future rollback action) is rejected rather than
    /// silently ignored, since rollback stays endpoint/cockpit-driven (#2563).
    /// </summary>
    public string Action { get; init; } = MetadataReleaseOperationExecutor.CreateAction;

    /// <summary>Release package identifier the lifecycle advances.</summary>
    public required string PackageId { get; init; }

    /// <summary>Target environment whose current graph snapshot is the rollback target.</summary>
    public required string TargetEnvironment { get; init; }

    /// <summary>Semantic identifier of the resource/layer being evolved and smoke-checked.</summary>
    public required string ResourceSemanticId { get; init; }

    /// <summary>Name of the newly added field the smoke stage asserts is present.</summary>
    public required string NewFieldName { get; init; }

    /// <summary>Canonical type of the new field (for example <c>String</c> or <c>Integer</c>).</summary>
    public string? NewFieldType { get; init; }

    /// <summary>Optional ETL/data-populate workload identifier dispatched after the schema change.</summary>
    public string? DataPopulateWorkloadId { get; init; }

    /// <summary>Stable script identifier for the additive change.</summary>
    public string? ScriptId { get; init; }

    /// <summary>Parses and validates a serialized payload, returning a descriptive error on failure.</summary>
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

    /// <summary>Builds the executable additive script plan the control service consumes.</summary>
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
