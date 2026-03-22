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
/// Admin endpoints for manifest drift detection and version history.
/// </summary>
internal static class AdminManifestDriftEndpoints
{
    /// <summary>
    /// Map manifest drift and version history endpoints to the admin API group.
    /// </summary>
    public static void MapAdminManifestDriftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Manifest")
            .RequireAdminAuthorization();

        _ = group.Map("/manifest/drift", HandleGetDriftReport)
            .WithName("GetManifestDriftReport")
            .WithSummary("Get drift report between declared and actual state")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/manifest/versions", HandleListVersions)
            .WithName("ListManifestVersions")
            .WithSummary("List stored manifest versions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/manifest/versions/{versionId}", HandleGetVersion)
            .WithName("GetManifestVersion")
            .WithSummary("Get a specific manifest version by ID")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task HandleGetDriftReport(
        HttpContext context,
        [FromServices] IMetadataResourceStore store,
        [FromServices] IManifestVersionStore versionStore)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var verbose = string.Equals(
            context.Request.Query["verbose"].ToString(), "true",
            StringComparison.OrdinalIgnoreCase);

        // Get the latest manifest version as baseline
        var baseline = await versionStore.GetLatestAsync(context.RequestAborted);

        // Get actual resources from the store
        var actualResources = await store.ListAsync(cancellationToken: context.RequestAborted);

        var driftRecords = baseline != null
            ? ManifestHashHelper.ComputeDrift(baseline.ManifestJson, actualResources, verbose)
            : new List<ManifestDriftRecord>();

        var report = new ManifestDriftReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            BaselineVersionId = baseline?.VersionId,
            HasDrift = driftRecords.Count > 0,
            Resources = driftRecords
        };

        var payload = ApiResponse<ManifestDriftReport>.CreateSuccess(report);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseManifestDriftReport);
    }

    private static async Task HandleListVersions(
        HttpContext context,
        [FromServices] IManifestVersionStore versionStore)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var limitStr = context.Request.Query["limit"].ToString();
        var offsetStr = context.Request.Query["offset"].ToString();
        var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 100) : 50;
        var offset = int.TryParse(offsetStr, out var o) ? Math.Max(0, o) : 0;

        var versions = await versionStore.ListAsync(limit, offset, context.RequestAborted);

        var response = new ManifestVersionListResponse
        {
            Versions = versions.Select(v => new ManifestVersionResponse
            {
                VersionId = v.VersionId,
                ManifestHash = v.ManifestHash,
                Summary = v.Summary,
                Actor = v.Actor,
                AppliedAt = v.AppliedAt,
                ResourceCount = v.ResourceCount
            }).ToArray()
        };

        var payload = ApiResponse<ManifestVersionListResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseManifestVersionListResponse);
    }

    private static async Task HandleGetVersion(
        HttpContext context,
        [FromServices] IManifestVersionStore versionStore)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var versionId = (string?)context.Request.RouteValues["versionId"];
        if (string.IsNullOrWhiteSpace(versionId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Version ID is required.");
            return;
        }

        var version = await versionStore.GetAsync(versionId, context.RequestAborted);
        if (version == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Manifest version '{versionId}' not found.");
            return;
        }

        var response = new ManifestVersionDetailResponse
        {
            VersionId = version.VersionId,
            ManifestHash = version.ManifestHash,
            Summary = version.Summary,
            Actor = version.Actor,
            AppliedAt = version.AppliedAt,
            ResourceCount = version.ResourceCount,
            Manifest = version.ManifestJson
        };

        var payload = ApiResponse<ManifestVersionDetailResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseManifestVersionDetailResponse);
    }

}
