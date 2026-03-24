// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for GitOps git repository watching and change management.
/// </summary>
internal static class AdminGitOpsWatchEndpoints
{
    /// <summary>
    /// Map GitOps watch endpoints to the admin API group.
    /// </summary>
    public static void MapAdminGitOpsWatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/gitops")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "GitOpsWatch")
            .RequireAdminAuthorization();

        _ = group.Map("/watch", HandleConfigureWatch)
            .WithName("ConfigureGitOpsWatch")
            .WithSummary("Configure or update git repository watch")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post, HttpMethods.Put }));

        _ = group.Map("/watch", HandleGetWatch)
            .WithName("GetGitOpsWatch")
            .WithSummary("Get current git repository watch configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/watch", HandleDeleteWatch)
            .WithName("DeleteGitOpsWatch")
            .WithSummary("Remove git repository watch configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        _ = group.Map("/changes", HandleListChanges)
            .WithName("ListGitOpsChanges")
            .WithSummary("List change history from watched repository")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/changes/{id}", HandleGetChange)
            .WithName("GetGitOpsChange")
            .WithSummary("Get details of a specific change")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/changes/{id}/diff", HandleGetChangeDiff)
            .WithName("GetGitOpsChangeDiff")
            .WithSummary("Get manifest diff (before/after) for a change")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task HandleConfigureWatch(
        HttpContext context,
        GitOpsWatchConfigRequest request,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Repository URL is required.");
            return;
        }

        // Reject URLs that could be interpreted as git CLI options
        if (request.RepositoryUrl.StartsWith('-'))
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Repository URL must not start with '-'.");
            return;
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;
        if (!IsValidGitRef(branch))
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Branch name contains invalid characters.");
            return;
        }

        var manifestPath = string.IsNullOrWhiteSpace(request.ManifestPath) ? "manifests/" : request.ManifestPath;
        if (!IsRelativePath(manifestPath))
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Manifest path must be a relative path without '..' segments.");
            return;
        }

        var minPoll = options.Value.MinPollIntervalSeconds;
        var pollInterval = Math.Max(minPoll, request.PollIntervalSeconds);

        var existing = await store.GetConfigAsync(context.RequestAborted);
        var now = DateTimeOffset.UtcNow;

        var config = new GitOpsWatchConfig
        {
            ConfigId = existing?.ConfigId ?? Guid.NewGuid(),
            RepositoryUrl = request.RepositoryUrl,
            Branch = branch,
            ManifestPath = manifestPath,
            PollIntervalSeconds = pollInterval,
            ApprovalRequired = request.ApprovalRequired,
            Enabled = request.Enabled,
            LastKnownCommitSha = existing?.LastKnownCommitSha,
            LastPolledAt = existing?.LastPolledAt,
            ConfiguredBy = request.ConfiguredBy ?? context.User.Identity?.Name,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };

        await store.UpsertConfigAsync(config, context.RequestAborted);

        var response = MapToResponse(config);
        var isCreate = existing == null;

        if (isCreate)
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
        }

        var payload = ApiResponse<GitOpsWatchConfigResponse>.CreateSuccess(response,
            isCreate ? "Git repository watch configured." : "Git repository watch updated.");
        await AdminResponseWriter.WriteJsonAsync(context, payload, GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);
    }

    private static async Task HandleGetWatch(
        HttpContext context,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        var config = await store.GetConfigAsync(context.RequestAborted);
        if (config == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                "No git repository watch is configured.");
            return;
        }

        var response = MapToResponse(config);
        var payload = ApiResponse<GitOpsWatchConfigResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);
    }

    private static async Task HandleDeleteWatch(
        HttpContext context,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        var config = await store.GetConfigAsync(context.RequestAborted);
        if (config == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                "No git repository watch is configured.");
            return;
        }

        await store.DeleteConfigAsync(config.ConfigId, context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task HandleListChanges(
        HttpContext context,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        _ = int.TryParse(context.Request.Query["limit"], out var limit);
        _ = int.TryParse(context.Request.Query["offset"], out var offset);
        if (limit <= 0)
        {
            limit = 100;
        }

        var records = await store.ListChangeRecordsAsync(limit, offset, context.RequestAborted);
        var responses = records.Select(MapToChangeResponse).ToArray();
        var payload = ApiResponse<GitOpsChangeRecordResponse[]>.CreateSuccess(responses);
        await AdminResponseWriter.WriteJsonAsync(context, payload, GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeRecordResponseArray);
    }

    private static async Task HandleGetChange(
        HttpContext context,
        Guid id,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        var record = await store.GetChangeRecordAsync(id, context.RequestAborted);
        if (record == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Change record '{id}' not found.");
            return;
        }

        var response = MapToChangeResponse(record);
        var payload = ApiResponse<GitOpsChangeRecordResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeRecordResponse);
    }

    private static async Task HandleGetChangeDiff(
        HttpContext context,
        Guid id,
        [FromServices] IGitOpsWatchStore store,
        [FromServices] IOptions<GitOpsWatchOptions> options)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        if (!options.Value.Enabled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "GitOps repository watch requires the enterprise edition.");
            return;
        }

        var record = await store.GetChangeRecordAsync(id, context.RequestAborted);
        if (record == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Change record '{id}' not found.");
            return;
        }

        var response = new GitOpsChangeDiffResponse
        {
            ChangeId = record.ChangeId,
            CommitSha = record.CommitSha,
            Before = record.ManifestBefore,
            After = record.ManifestAfter
        };
        var payload = ApiResponse<GitOpsChangeDiffResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeDiffResponse);
    }

    internal static GitOpsWatchConfigResponse MapToResponse(GitOpsWatchConfig config) => new()
    {
        ConfigId = config.ConfigId,
        RepositoryUrl = config.RepositoryUrl,
        Branch = config.Branch,
        ManifestPath = config.ManifestPath,
        PollIntervalSeconds = config.PollIntervalSeconds,
        ApprovalRequired = config.ApprovalRequired,
        Enabled = config.Enabled,
        LastKnownCommitSha = config.LastKnownCommitSha,
        LastPolledAt = config.LastPolledAt,
        ConfiguredBy = config.ConfiguredBy,
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt
    };

    private static GitOpsChangeRecordResponse MapToChangeResponse(GitOpsChangeRecord record) => new()
    {
        ChangeId = record.ChangeId,
        ConfigId = record.ConfigId,
        CommitSha = record.CommitSha,
        CommitMessage = record.CommitMessage,
        CommitAuthor = record.CommitAuthor,
        CommitTimestamp = record.CommitTimestamp,
        Status = MapChangeStatusString(record.Status),
        PendingApprovalId = record.PendingApprovalId,
        ApplySummary = record.ApplySummary,
        ErrorMessage = record.ErrorMessage,
        DetectedAt = record.DetectedAt,
        AppliedAt = record.AppliedAt
    };

    private static string MapChangeStatusString(GitOpsChangeStatus status) => status switch
    {
        GitOpsChangeStatus.Applied => "applied",
        GitOpsChangeStatus.PendingApproval => "pending_approval",
        GitOpsChangeStatus.Failed => "failed",
        GitOpsChangeStatus.Skipped => "skipped",
        _ => "applied"
    };

    /// <summary>
    /// Validates that a git ref name contains only safe characters and no '..' sequences.
    /// Prevents git option injection via branch names starting with '-'.
    /// </summary>
    internal static bool IsValidGitRef(string refName)
    {
        if (string.IsNullOrEmpty(refName) || refName.StartsWith('-') || refName.Contains(".."))
        {
            return false;
        }

        foreach (var c in refName)
        {
            if (char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates that a path is relative and does not contain traversal sequences.
    /// Prevents path traversal via absolute paths or '..' segments.
    /// </summary>
    internal static bool IsRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(".."))
        {
            return false;
        }

        return true;
    }
}
