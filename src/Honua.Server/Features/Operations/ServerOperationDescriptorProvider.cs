// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Surfaces the server-side operation descriptors into the grounding catalog. Today it
/// contributes the <c>service.publish</c> descriptor and the catalog-only
/// <c>admin.server.status</c> convention sample; DevOps descriptors join the catalog through
/// their own provider in a later phase (descriptors only — execution stays remote).
/// </summary>
internal sealed class ServerOperationDescriptorProvider : IOperationDescriptorProvider
{
    private readonly IReadOnlyList<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor> _legacyActuators;

    public ServerOperationDescriptorProvider()
        : this([])
    {
    }

    public ServerOperationDescriptorProvider(
        IEnumerable<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor> legacyActuators)
        => _legacyActuators = legacyActuators.ToArray();

    /// <inheritdoc />
    public string ProviderId => ServicePublishOperation.ProviderId;

    /// <inheritdoc />
    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(
        [
            ServicePublishOperation.BuildDescriptor(),
            .. StudioDraftOperations.BuildDescriptors(),
            .. WorkflowRollbackOperations.BuildDescriptors(),
            BuildAdminServerStatusDescriptor(),
            .. _legacyActuators.Select(actuator => BuildLegacyDescriptor(actuator.OperationClass)),
            .. AdminOperateOperationCatalog.Descriptors
        ]);

    private static OperationDescriptor BuildLegacyDescriptor(
        Honua.Core.Features.Guardrails.Domain.OperationClass operationClass) => new()
        {
            OperationId = LegacyOperationIds.For(operationClass),
            ProviderId = ServicePublishOperation.ProviderId,
            Title = $"Control-plane {operationClass}",
            Description = "Compatibility descriptor routed through the canonical durable operation runtime.",
            Category = "control-plane",
            ExecutionKind = OperationExecutionKind.Job,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            IsCompatibilityOnly = true,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.DeploymentScope,
                SideEffectClass = OperationSideEffectClass.MutatesMetadata,
                Determinism = OperationDeterminism.RuntimeDynamic,
                SupportsDryRun = true,
            },
            InputSchema = [],
            OutputSchema = [],
        };

    private static OperationDescriptor BuildAdminServerStatusDescriptor() => new()
    {
        OperationId = "admin.server.status",
        ProviderId = ServicePublishOperation.ProviderId,
        Title = "Read server status",
        Description = "Reads the server readiness and version status without changing durable state.",
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.None,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false
        },
        InputSchema = [],
        OutputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = "status",
                Title = "Server status",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            },
            new OperationParameterDescriptor
            {
                Name = "version",
                Title = "Server version",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            }
        ]
    };
}
