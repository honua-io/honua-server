// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>Save and reopen adapters use the same authorized, durable runtime as Studio REST.</summary>
internal abstract class StudioVersionToolBase(IGeoprocessingJobService jobService, ILogger logger)
    : StudioDraftToolBase(jobService, logger)
{
    protected static StudioDraftMutationContext MutationContext(HttpContext context, ClaimsPrincipal principal) => new()
    {
        PrincipalId = McpAuthorizationHelper.ResolveActorId(principal),
        TenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId,
        SchemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
        CorrelationId = context.TraceIdentifier,
        AuthorizationOutcome = "authorized",
        Roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
        ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
        RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
            .OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
    };
}

internal sealed class SaveStudioVersionTool(IGeoprocessingJobService jobService, ILogger<SaveStudioVersionTool> logger)
    : StudioVersionToolBase(jobService, logger), IMcpTool
{
    public const string ToolName = "honua_studio_save_version";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Save Studio version",
        Description = "Save the current Studio draft generation as an immutable version through the governed Studio runtime. "
            + "Use the returned version.itemId, version.versionId and version.contentHash for publication or reopen. "
            + "Read the draft again before editing it further because saving refreshes its generation. "
            + "An approval-required operation has no saved version yet; follow its proposal before continuing.",
        InputSchema = StudioMcpSchemas.SaveVersionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioVersionMutationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Save Studio version", destructive: false, idempotent: false),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create,
            StudioAuthorizationOperation.CreateVersion, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioSaveVersionArgument);
        if (argument.DraftId == Guid.Empty || argument.Generation < 1 || argument.ChangeNote?.Length > StudioMcpSchemas.MaxNoteLength)
        {
            throw new GeoprocessingValidationException("A draftId and positive generation are required; changeNote is limited to 2000 characters.");
        }
        var lifecycle = RequireLifecycleService(httpContext);
        var authorization = RequireAuthorizationService(httpContext);
        var draft = await RequireAuthorizedDraftAsync(httpContext, principal, lifecycle, argument.DraftId,
            StudioAuthorizationOperation.CreateVersion, OperatorOperation.Create, cancellationToken).ConfigureAwait(false);
        var pointers = await lifecycle.GetPointersAsync(draft.ItemId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException("Studio content item was not found.");
        // Saving advances the parent item's pointer, so both owners must authorize it.
        await EnsureStudioAuthorizedAsync(httpContext, authorization, principal, StudioAuthorizationOperation.CreateVersion,
            pointers.OwnerId, draft.ItemId.ToString("D"), "studio-content-item", OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);
        RequireAuthorizedGeneration(draft, argument.Generation);
        var actor = ActorIdFor(authorization, principal);
        var receipt = await RequireMutationRuntime(httpContext).SaveVersionAsync(argument.DraftId, argument.Generation,
            argument.ChangeNote, actor, MutationContext(httpContext, principal), cancellationToken).ConfigureAwait(false);
        long? generationAfter = receipt.Value is not null
            ? (await RequireAuthorizedDraftAsync(httpContext, principal, lifecycle, draft.DraftId,
                StudioAuthorizationOperation.CreateVersion, OperatorOperation.Create, cancellationToken)
                .ConfigureAwait(false)).Generation
            : null;
        Audit(principal, Name, draft.DraftId, draft.Generation, generationAfter);
        return McpToolHelpers.SuccessResult(new McpStudioSaveVersionOutput
        {
            Operation = receipt.Operation,
            Version = receipt.Value,
        }, StudioMcpJsonContext.Default.McpStudioSaveVersionOutput);
    }
}

internal sealed class ReopenStudioVersionTool(IGeoprocessingJobService jobService, ILogger<ReopenStudioVersionTool> logger)
    : StudioVersionToolBase(jobService, logger), IMcpTool
{
    public const string ToolName = "honua_studio_reopen_version";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Reopen Studio version",
        Description = "Create an editable Studio draft from a saved version through the governed Studio runtime. "
            + "Use its draftId and generation for subsequent edits. An approval-required operation has no reopened draft yet.",
        InputSchema = StudioMcpSchemas.ReopenVersionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftMutationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Reopen Studio version", destructive: false, idempotent: false),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create,
            StudioAuthorizationOperation.ReopenVersion, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioReopenVersionArgument);
        if (argument.ItemId == Guid.Empty || argument.VersionId == Guid.Empty)
        {
            throw new GeoprocessingValidationException("itemId and versionId are required.");
        }
        var lifecycle = RequireLifecycleService(httpContext);
        var authorization = RequireAuthorizationService(httpContext);
        var version = await lifecycle.GetVersionAsync(argument.ItemId, argument.VersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException("Studio content version was not found.");
        await EnsureStudioAuthorizedAsync(httpContext, authorization, principal, StudioAuthorizationOperation.ReopenVersion,
            version.OwnerId, argument.ItemId.ToString("D"), "studio-content-item", OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);
        var actor = ActorIdFor(authorization, principal);
        var receipt = await RequireMutationRuntime(httpContext).ReopenVersionAsync(argument.ItemId, argument.VersionId,
            actor, MutationContext(httpContext, principal), cancellationToken).ConfigureAwait(false);
        if (receipt.Value is { } draft)
        {
            Audit(principal, Name, draft.DraftId, null, draft.Generation);
        }
        return McpToolHelpers.SuccessResult(McpStudioDraftMutationOutput.From(receipt),
            StudioMcpJsonContext.Default.McpStudioDraftMutationOutput);
    }
}
