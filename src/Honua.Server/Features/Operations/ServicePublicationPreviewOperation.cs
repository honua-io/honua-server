// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

internal static class ServicePublicationPreviewOperation
{
    public const string OperationId = "service.publication.preview";

    public static OperationDescriptor BuildDescriptor() => ServicePublishOperation.BuildDescriptor() with
    {
        OperationId = OperationId,
        Title = "Preview publication",
        Description = "Validate a saved-connection table and preview its canonical publication metadata without publishing. "
            + "Returns an actor-bound, expiring previewReference for the identical service.publish request. "
            + "Planned layer identities are placeholders; no route or layer ID is allocated.",
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.None,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false
        },
        InputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = "connectionId", Title = "Saved connection id", Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            },
            new OperationParameterDescriptor
            {
                Name = "serviceName", Title = "Service name", Required = false,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            },
            new OperationParameterDescriptor
            {
                Name = "fields", Title = "Source field names; omitted selects all", Required = false,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.List,
                    Items = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text } }
            },
            .. ServicePublishOperation.BuildDescriptor().InputSchema
                .Where(parameter => parameter.Name != PublicationPreviewBindings.ReferenceKey)
        ],
        OutputSchema = [
            new OperationParameterDescriptor
            {
                Name = "previewReference", Title = "Retained preview reference", Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            }
        ]
    };
}
