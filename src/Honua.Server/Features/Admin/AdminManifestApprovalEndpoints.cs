// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for manifest approval workflows.
/// </summary>
internal static class AdminManifestApprovalEndpoints
{
    private const int HistoryPageSize = 200;

    /// <summary>
    /// Map manifest approval endpoints to the admin API group.
    /// </summary>
    public static void MapAdminManifestApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/manifest/pending")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "ManifestApproval")
            .RequireAdminAuthorization();

        _ = group.Map("/", HandleListPending)
            .WithName("ListManifestPendingChanges")
            .WithSummary("List pending manifest changes awaiting approval")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/{id}", HandleGetPending)
            .WithName("GetManifestPendingChange")
            .WithSummary("Get details of a specific pending manifest change")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/{id}/approve", HandleApprove)
            .WithName("ApproveManifestPendingChange")
            .WithSummary("Approve and apply a pending manifest change")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/{id}/reject", HandleReject)
            .WithName("RejectManifestPendingChange")
            .WithSummary("Reject a pending manifest change")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/history", HandleHistory)
            .WithName("GetManifestApprovalHistory")
            .WithSummary("Query approval history for manifest changes")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task HandleListPending(
        HttpContext context,
        [FromServices] ManifestApprovalGate approvalGate)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!approvalGate.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Manifest approval workflows require the enterprise edition.");
            return;
        }

        var statusFilter = context.Request.Query["status"].ToString();
        ManifestApprovalStatus? status = string.IsNullOrWhiteSpace(statusFilter)
            ? ManifestApprovalStatus.Pending
            : ParseStatus(statusFilter);

        var pending = await approvalGate.PendingStore.ListAsync(status, cancellationToken: context.RequestAborted);
        var responses = pending.Select(AdminMetadataEndpoints.MapToResponse).ToArray();
        var payload = ApiResponse<ManifestPendingChangeResponse[]>.CreateSuccess(responses);
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponseArray);
    }

    private static async Task HandleGetPending(
        HttpContext context,
        Guid id,
        [FromServices] ManifestApprovalGate approvalGate)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!approvalGate.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Manifest approval workflows require the enterprise edition.");
            return;
        }

        var pending = await approvalGate.PendingStore.GetAsync(id, context.RequestAborted);
        if (pending == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Pending change '{id}' not found.");
            return;
        }

        var response = AdminMetadataEndpoints.MapToResponse(pending);
        var payload = ApiResponse<ManifestPendingChangeResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);
    }

    private static async Task HandleApprove(
        HttpContext context,
        Guid id,
        ManifestApproveRequest request,
        [FromServices] ManifestApprovalGate approvalGate,
        [FromServices] IMetadataResourceStore resourceStore,
        [FromServices] IMetadataSchemaRegistry schemaRegistry,
        [FromServices] IMetadataCompiler compiler,
        [FromServices] IManifestVersionStore versionStore)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!approvalGate.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Manifest approval workflows require the enterprise edition.");
            return;
        }

        var pending = await approvalGate.PendingStore.GetAsync(id, context.RequestAborted);
        if (pending == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Pending change '{id}' not found.");
            return;
        }

        if (pending.Status != ManifestApprovalStatus.Pending)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status409Conflict,
                $"Pending change '{id}' has already been {pending.Status.ToString().ToLowerInvariant()}.");
            return;
        }

        // Deserialize and validate before touching status — if the snapshot is
        // corrupt or resources no longer pass validation, we reject early and
        // leave the record in 'pending' so the operator can inspect it.
        var applyRequest = JsonSerializer.Deserialize(
            pending.ManifestSnapshot.GetRawText(),
            MetadataResourceJsonContext.Default.ManifestApplyRequest);

        if (applyRequest?.Resources == null || applyRequest.Resources.Count == 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                "Failed to deserialize queued manifest snapshot.");
            return;
        }

        var normalizedResources = new List<MetadataResource>();
        var skippedResources = new List<string>();
        foreach (var resource in applyRequest.Resources)
        {
            var normalized = AdminMetadataEndpoints.NormalizeResource(resource, null, null);
            var validation = schemaRegistry.ValidateAndUpgrade(normalized);
            if (!validation.IsValid || validation.Resource == null)
            {
                var resourceName = resource.Metadata?.Name ?? "(unknown)";
                skippedResources.Add($"{resource.Kind}/{resourceName}: {string.Join(" ", validation.Errors)}");
                continue;
            }

            normalizedResources.Add(validation.Resource);
        }

        if (normalizedResources.Count == 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status422UnprocessableEntity,
                $"All {skippedResources.Count} resource(s) failed re-validation and cannot be applied: {string.Join("; ", skippedResources)}");
            return;
        }

        var reserved = await approvalGate.PendingStore.UpdateDecisionAsync(
            id,
            ManifestApprovalStatus.Applying,
            request.ApprovedBy,
            request.Reason,
            expectedCurrentStatus: ManifestApprovalStatus.Pending,
            cancellationToken: context.RequestAborted);

        if (!reserved)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status409Conflict,
                $"Pending change '{id}' could not be approved. It may have already been decided.");
            return;
        }

        ManifestApplyResult applyResult;
        try
        {
            applyResult = await AdminMetadataEndpoints.ApplyNormalizedResourcesAsync(
                normalizedResources,
                applyRequest.DryRun,
                applyRequest.Prune,
                resourceStore,
                compiler,
                context.RequestAborted,
                versionStore,
                request.ApprovedBy);
        }
        catch (Exception ex)
        {
            var reset = await approvalGate.PendingStore.UpdateDecisionAsync(
                id,
                ManifestApprovalStatus.Pending,
                null,
                null,
                expectedCurrentStatus: ManifestApprovalStatus.Applying,
                cancellationToken: CancellationToken.None);

            if (!reset)
            {
                throw new InvalidOperationException(
                    $"Pending change '{id}' could not be returned to pending after an apply failure.",
                    ex);
            }

            throw;
        }

        var updated = await approvalGate.PendingStore.UpdateDecisionAsync(
            id,
            ManifestApprovalStatus.Approved,
            request.ApprovedBy,
            request.Reason,
            expectedCurrentStatus: ManifestApprovalStatus.Applying,
            context.RequestAborted);

        if (!updated)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                $"Manifest change '{id}' was applied but the approval record could not be finalized.");
            return;
        }

        approvalGate.EnqueueWebhook(new ManifestApprovalWebhookEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "manifest-approved",
            PendingId = id,
            ManifestHash = pending.ManifestHash,
            Status = "approved",
            Actor = request.ApprovedBy,
            Reason = request.Reason,
            ResourceCount = pending.ResourceCount,
            Timestamp = DateTimeOffset.UtcNow
        });

        var message = skippedResources.Count > 0
            ? $"Manifest change approved and applied. {skippedResources.Count} resource(s) skipped re-validation: {string.Join("; ", skippedResources)}"
            : "Manifest change approved and applied.";
        var payload = ApiResponse<ManifestApplyResult>.CreateSuccess(applyResult, message);
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestApplyResult);
    }

    private static async Task HandleReject(
        HttpContext context,
        Guid id,
        ManifestRejectRequest request,
        [FromServices] ManifestApprovalGate approvalGate)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!approvalGate.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Manifest approval workflows require the enterprise edition.");
            return;
        }

        var pending = await approvalGate.PendingStore.GetAsync(id, context.RequestAborted);
        if (pending == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Pending change '{id}' not found.");
            return;
        }

        if (pending.Status != ManifestApprovalStatus.Pending)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status409Conflict,
                $"Pending change '{id}' has already been {pending.Status.ToString().ToLowerInvariant()}.");
            return;
        }

        var updated = await approvalGate.PendingStore.UpdateDecisionAsync(
            id,
            ManifestApprovalStatus.Rejected,
            request.RejectedBy,
            request.Reason,
            expectedCurrentStatus: ManifestApprovalStatus.Pending,
            cancellationToken: context.RequestAborted);

        if (!updated)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status409Conflict,
                $"Pending change '{id}' could not be rejected. It may have already been decided.");
            return;
        }

        approvalGate.EnqueueWebhook(new ManifestApprovalWebhookEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "manifest-rejected",
            PendingId = id,
            ManifestHash = pending.ManifestHash,
            Status = "rejected",
            Actor = request.RejectedBy,
            Reason = request.Reason,
            ResourceCount = pending.ResourceCount,
            Timestamp = DateTimeOffset.UtcNow
        });

        var updatedChange = await approvalGate.PendingStore.GetAsync(id, context.RequestAborted);
        var response = AdminMetadataEndpoints.MapToResponse(updatedChange ?? pending);
        var payload = ApiResponse<ManifestPendingChangeResponse>.CreateSuccess(response, "Manifest change rejected.");
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);
    }

    private static async Task HandleHistory(
        HttpContext context,
        [FromServices] ManifestApprovalGate approvalGate)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!approvalGate.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "Manifest approval workflows require the enterprise edition.");
            return;
        }

        var all = new List<ManifestPendingChange>();
        var offset = 0;
        while (true)
        {
            var page = await approvalGate.PendingStore.ListAsync(
                status: null,
                limit: HistoryPageSize,
                offset: offset,
                cancellationToken: context.RequestAborted);
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);
            if (page.Count < HistoryPageSize)
            {
                break;
            }

            offset += page.Count;
        }

        var responses = all.Select(AdminMetadataEndpoints.MapToResponse).ToArray();
        var payload = ApiResponse<ManifestPendingChangeResponse[]>.CreateSuccess(responses);
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponseArray);
    }

    private static ManifestApprovalStatus? ParseStatus(string status) => status.ToLowerInvariant() switch
    {
        "pending" => ManifestApprovalStatus.Pending,
        "applying" => ManifestApprovalStatus.Applying,
        "approved" => ManifestApprovalStatus.Approved,
        "rejected" => ManifestApprovalStatus.Rejected,
        "expired" => ManifestApprovalStatus.Expired,
        _ => null
    };
}
