// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Shared identity and descriptor definition for the <c>service.publish</c> operation. Keeps
/// the operation id and its groundable descriptor in one place so the descriptor provider and
/// the executor agree on the contract.
/// </summary>
internal static class ServicePublishOperation
{
    /// <summary>
    /// Stable operation identifier for publishing a PostGIS table as a Honua layer.
    /// </summary>
    public const string OperationId = "service.publish";

    /// <summary>
    /// Provider identifier for the server-side publish descriptor provider.
    /// </summary>
    public const string ProviderId = "honua.server.operations";

    /// <summary>
    /// Builds the groundable descriptor for <c>service.publish</c>. ExecutionKind is
    /// synchronous (publish is a 12-step inline transaction); ApprovalModel is
    /// <see cref="OperationApprovalModel.OperatorGate"/> — publish is gated by the operator
    /// approval gate at the HTTP layer, NOT the Studio publish-request lane.
    /// </summary>
    public static OperationDescriptor BuildDescriptor() => new()
    {
        OperationId = OperationId,
        ProviderId = ProviderId,
        Title = "Publish service layer",
        Description = "Publishes a PostGIS table as a Honua layer in a service, projecting it into the Metadata v2 graph.",
        Category = "service",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true
        },
        InputSchema =
        [
            Required("schema", "Source schema", WorkflowSchemaValueType.Text),
            Required("table", "Source table", WorkflowSchemaValueType.Text),
            Required("layerName", "Layer name", WorkflowSchemaValueType.Text),
            Optional("geometryColumn", "Geometry column", WorkflowSchemaValueType.Text),
            Optional("geometryType", "Geometry type", WorkflowSchemaValueType.Text),
            Optional("primaryKey", "Primary key", WorkflowSchemaValueType.Text),
            Optional("srid", "Target SRID", WorkflowSchemaValueType.WholeNumber),
            Optional("description", "Description", WorkflowSchemaValueType.Text)
        ],
        OutputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = "layerId",
                Title = "Published layer id",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber }
            },
            new OperationParameterDescriptor
            {
                Name = "metadataRevision",
                Title = "Metadata v2 revision",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber }
            }
        ]
    };

    private static OperationParameterDescriptor Required(string name, string title, WorkflowSchemaValueType type)
        => new()
        {
            Name = name,
            Title = title,
            Required = true,
            Schema = new WorkflowSchemaDefinition { Type = type }
        };

    private static OperationParameterDescriptor Optional(string name, string title, WorkflowSchemaValueType type)
        => new()
        {
            Name = name,
            Title = title,
            Required = false,
            Schema = new WorkflowSchemaDefinition { Type = type }
        };
}
