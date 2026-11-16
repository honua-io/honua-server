// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

internal static class ServicePublishOperation
{
    public const string OperationId = "service.publish";

    public const string ProviderId = "honua.server.operations";

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
            Optional("folderId", "Folder id; omitted preserves membership, null selects root for a new service; use service.move to change existing membership", WorkflowSchemaValueType.Text),
            Optional("publicName", "Immutable public service name", WorkflowSchemaValueType.Text),
            Optional("namespace", "Publication namespace; omitted is unscoped", WorkflowSchemaValueType.Text),
            Optional("description", "Description", WorkflowSchemaValueType.Text)
        ],
        OutputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = "storageLayerId",
                Title = "Published storage layer id",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber }
            },
            new OperationParameterDescriptor
            {
                Name = "serviceLayerIndex",
                Title = "Published service layer index",
                Required = false,
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
