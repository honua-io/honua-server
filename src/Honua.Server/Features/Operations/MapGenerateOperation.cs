// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Shared identity and descriptor definition for the <c>map.generate</c> Studio operation.
/// Keeps the operation id and its groundable descriptor in one place so the descriptor
/// provider and the executor agree on the contract.
/// </summary>
/// <remarks>
/// This is the strangler proof for the toolset: <c>map.generate</c> is a Studio <em>generator</em>,
/// not a flat sync side effect. It produces a DRAFT map package that enters the Studio
/// draft → version → publish-request lifecycle, so its <see cref="OperationApprovalModel"/> is
/// <see cref="OperationApprovalModel.StudioPublishRequest"/> (the lane the produced draft awaits),
/// and its determinism is <see cref="OperationDeterminism.AiAssisted"/>. The same Validate /
/// Submit→Handle / GetStatus contract that models the synchronous publish executor also models a
/// generator that yields a draft-entering-a-lane — proving the toolset absorbs generators.
/// </remarks>
internal static class MapGenerateOperation
{
    /// <summary>
    /// Stable operation identifier for generating a draft map package from a prompt.
    /// </summary>
    public const string OperationId = "map.generate";

    /// <summary>
    /// Provider identifier for the server-side Studio generation descriptor provider.
    /// </summary>
    public const string ProviderId = "honua.server.operations";

    /// <summary>
    /// Approval lane the produced draft awaits: the Studio publish-request lifecycle.
    /// </summary>
    public const string StudioPublishRequestLane = "studio-publish-request";

    /// <summary>
    /// Builds the groundable descriptor for <c>map.generate</c>. ExecutionKind is
    /// <see cref="OperationExecutionKind.Synchronous"/> (the draft is produced inline within
    /// the request); ApprovalModel is <see cref="OperationApprovalModel.StudioPublishRequest"/>
    /// — the generated map is a draft entering the Studio draft → version → publish-request
    /// lifecycle, NOT an operator-gated mutation. Policy advertises a draft/workspace blast
    /// radius, a creates-draft side effect, AI-assisted determinism, and no dry run.
    /// </summary>
    public static OperationDescriptor BuildDescriptor() => new()
    {
        OperationId = OperationId,
        ProviderId = ProviderId,
        Title = "Generate map draft",
        Description = "Generates a draft map package from a natural-language prompt, entering the Studio draft → version → publish-request lifecycle.",
        Category = "Studio/Generation",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.StudioPublishRequest,
        Policy = new OperationPolicyMetadata
        {
            // BlastRadius=Draft/Workspace scope: the generated draft is a new resource that
            // only enters the workspace as a draft; it does not mutate published service state.
            BlastRadiusClass = OperationBlastRadiusClass.ResourceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.AiAssisted,
            SupportsDryRun = false
        },
        InputSchema =
        [
            Required("prompt", "Prompt", WorkflowSchemaValueType.Text),
            Optional("provider", "Generation provider", WorkflowSchemaValueType.Text),
            Optional("model", "Model override", WorkflowSchemaValueType.Text)
        ],
        OutputSchema =
        [
            new OperationParameterDescriptor
            {
                Name = "mapPackageId",
                Title = "Draft map package id",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            },
            new OperationParameterDescriptor
            {
                Name = "status",
                Title = "Generation status",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
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
