// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal sealed class ListStudioItemsTool(IGeoprocessingJobService jobs, ILogger<ListStudioItemsTool> logger)
    : StudioDraftToolBase(jobs, logger), IMcpTool
{
    public const string ToolName = "honua_studio_list_items";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name, Title = "List saved Studio items",
        Description = "Find saved maps and other Studio items in the caller's authorized owner and tenant scope. "
            + "Use items[].itemId and currentVersionId with honua_studio_get_version, or list its version history. Follow nextCursor for more items.",
        InputSchema = StudioDiscoverySchemas.ListItems,
        OutputSchema = StudioDiscoverySchemas.ItemsOutput,
        Annotations = McpToolAnnotationSets.ReadOnly("List saved Studio items")
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(context, OperatorOperation.Read,
            StudioAuthorizationOperation.ListOwn, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioListItemsArgument);
        if (argument.Limit is < 1 or > 100 || argument.Query?.Length > StudioMcpSchemas.MaxShortTextLength || argument.Cursor?.Length > StudioMcpSchemas.MaxNoteLength)
            throw new GeoprocessingValidationException("limit must be 1–100; q is limited to 200 characters and cursor to 2000.");
        var authorization = RequireAuthorizationService(context);
        var callerId = authorization.ResolveCallerId(principal);
        await EnsureStudioAuthorizedAsync(context, authorization, principal, StudioAuthorizationOperation.ListOwn,
            callerId, RequestTenantId(context), null, "studio-content-item", OperatorOperation.Read, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAdmin(principal) && authorization.IsEndUserAuthorizationEnabled && string.IsNullOrWhiteSpace(callerId))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var result = await RequireLifecycleService(context).ListContentItemsAsync(new StudioContentItemQuery
        {
            OwnerId = authorization.IsAdmin(principal) || !authorization.IsEndUserAuthorizationEnabled ? null : callerId,
            Tenant = authorization.CreateTenantScopeFilter(principal), SearchTerm = argument.Query,
            Cursor = argument.Cursor, Limit = argument.Limit
        }, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(result, StudioMcpJsonContext.Default.StudioContentItemListResult);
    }
}

internal sealed class ListStudioVersionsTool(IGeoprocessingJobService jobs, ILogger<ListStudioVersionsTool> logger)
    : StudioDraftToolBase(jobs, logger), IMcpTool
{
    public const string ToolName = "honua_studio_list_versions";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name, Title = "List saved Studio versions",
        Description = "List authorized immutable versions of an item, newest first, including versionId and contentHash. "
            + "Use nextBeforeVersionNumber as beforeVersionNumber to continue. No draft or publication pointer changes.",
        InputSchema = StudioDiscoverySchemas.ListVersions,
        OutputSchema = StudioDiscoverySchemas.VersionsOutput,
        Annotations = McpToolAnnotationSets.ReadOnly("List saved Studio versions")
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = await EnsureAuthorizedAsync(context, OperatorOperation.Read,
            StudioAuthorizationOperation.ReadContentItem, cancellationToken).ConfigureAwait(false);
        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioListVersionsArgument);
        if (argument.ItemId == Guid.Empty || argument.Limit is < 1 or > 100 || argument.BeforeVersionNumber is < 1)
            throw new GeoprocessingValidationException("itemId is required; limit must be 1–100 and beforeVersionNumber must be positive.");
        var lifecycle = RequireLifecycleService(context);
        var authorization = RequireAuthorizationService(context);
        var pointers = await lifecycle.GetPointersAsync(argument.ItemId, cancellationToken).ConfigureAwait(false);
        await EnsureStudioAuthorizedAsync(context, authorization, principal, StudioAuthorizationOperation.ReadContentItem,
            pointers?.OwnerId, pointers is null ? RequestTenantId(context) : pointers.TenantId,
            argument.ItemId.ToString("D"), "studio-content-item", OperatorOperation.Read, cancellationToken).ConfigureAwait(false);
        if (pointers is null)
            throw new GeoprocessingNotFoundException("Studio content item was not found.");
        var versions = await lifecycle.ListVersionsAsync(argument.ItemId, cancellationToken).ConfigureAwait(false);
        var visible = new List<McpStudioVersionSummary>();
        foreach (var version in versions.Where(version => !argument.BeforeVersionNumber.HasValue || version.VersionNumber < argument.BeforeVersionNumber.Value)
            .OrderByDescending(version => version.VersionNumber))
        {
            var decision = await authorization.AuthorizeAsync(principal, authorization.ResolveCallerId(principal),
                StudioAuthorizationOperation.ReadContentItem, version.OwnerId, version.TenantId,
                resourceId: version.VersionId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                continue;
            visible.Add(new(version.VersionId, version.VersionNumber, version.ContentHash, version.CreatedAt, version.ChangeNote));
            if (visible.Count > argument.Limit)
                break;
        }
        var page = visible.Take(argument.Limit).ToArray();
        return McpToolHelpers.SuccessResult(new McpStudioVersionPage(argument.ItemId, page,
            visible.Count > argument.Limit ? page[^1].VersionNumber : null), StudioMcpJsonContext.Default.McpStudioVersionPage);
    }
}
