// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>Restores an existing saved-version pointer through native rollback and separate agent approval.</summary>
internal sealed class ProposeStudioRestoreTool(IGeoprocessingJobService jobService, ILogger<ProposeStudioRestoreTool> logger)
    : StudioVersionToolBase(jobService, logger), IMcpTool
{
    public const string ToolName = "honua_studio_propose_restore";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Restore a saved Studio version",
        Description = "Propose restoration of the original saved version, using its contentHash and the current pointers "
            + "returned by honua_studio_get_version. Bind the exact pointer and observed pointerRevision. "
            + "Workspace agents require a separate human approval and an exact studio-restore item grant. "
            + "Editable drafts are preserved. Reuse the same idempotencyKey and identical arguments after a lost response; "
            + "a pending approval is not a completed restoration.",
        InputSchema = StudioMcpSchemas.ProposeRestoreArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioProposeRestoreOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Restore a saved Studio version", destructive: true, idempotent: true),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Rollback,
            StudioAuthorizationOperation.Rollback, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioProposeRestoreArgument);
        if (argument.ItemId == Guid.Empty || argument.TargetVersionId == Guid.Empty || argument.ExpectedCurrentVersionId == Guid.Empty ||
            argument.ExpectedPointerRevision < 0 || !StudioPackageEnumHelpers.IsDefined(argument.Pointer) ||
            string.IsNullOrWhiteSpace(argument.ContentHash) || argument.ContentHash.Length > 128 ||
            string.IsNullOrWhiteSpace(argument.IdempotencyKey) || argument.IdempotencyKey.Length > 200 || argument.Reason?.Length > 1000)
            throw new GeoprocessingValidationException("Exact item/version/hash, current pointer/revision and a stable idempotencyKey are required.");

        var lifecycle = RequireLifecycleService(httpContext);
        var authorization = RequireAuthorizationService(httpContext);
        var pointers = await lifecycle.GetPointersAsync(argument.ItemId, cancellationToken).ConfigureAwait(false);
        await EnsureStudioAuthorizedAsync(httpContext, authorization, principal, StudioAuthorizationOperation.Rollback,
            pointers?.OwnerId, pointers is null ? RequestTenantId(httpContext) : pointers.TenantId,
            argument.ItemId.ToString("D"), "studio-content-item", OperatorOperation.Rollback, cancellationToken).ConfigureAwait(false);
        if (pointers is null) throw new GeoprocessingNotFoundException("Studio content item was not found.");
        var actor = ActorIdFor(authorization, principal);
        var context = MutationContext(httpContext, principal) with
        {
            PrincipalId = actor,
            IdempotencyKey = argument.IdempotencyKey,
            RequiredApprovalPolicyRef = AgentCredentialAuthority.IsAgent(principal)
                ? "agent-workspace.human-approval" : null,
        };
        // The canonical runtime resolves idempotent operations before validating the old
        // pointer. The native store also recovers an already committed original receipt.
        var receipt = await RequireMutationRuntime(httpContext).RollbackAsync(
            argument.ItemId, argument.TargetVersionId, argument.Pointer, actor, argument.Reason,
            new StudioRollbackPreconditions
            {
                ContentHash = argument.ContentHash,
                ExpectedCurrentVersionId = argument.ExpectedCurrentVersionId,
                ExpectedPointerRevision = argument.ExpectedPointerRevision,
            }, context, cancellationToken).ConfigureAwait(false);
        var pending = receipt.Operation.Status == OperationHandleStatus.RequiresApproval;
        if (pending ? string.IsNullOrWhiteSpace(receipt.Operation.ProposalId)
            : receipt.Operation.Status != OperationHandleStatus.Completed || receipt.Value is null)
            throw new GeoprocessingPreconditionFailedException(receipt.Operation.Reason ?? "Studio restoration did not complete or enter durable approval.");
        return McpToolHelpers.SuccessResult(new McpStudioProposeRestoreOutput
        {
            Operation = receipt.Operation,
            Restoration = receipt.Value,
            HumanConfirmationRequired = pending,
            ProposalUri = pending ? McpResourceUris.ProposalUri(receipt.Operation.ProposalId!) : null,
            Message = pending ? "Restoration awaits a separate authorized human approval."
                : "The original saved-version pointer was restored; editable drafts were preserved.",
        }, StudioMcpJsonContext.Default.McpStudioProposeRestoreOutput);
    }
}
