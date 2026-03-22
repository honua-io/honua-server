// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for the feature overview page showing edition-gated features.
/// </summary>
internal static class FeatureOverviewEndpoints
{
    internal sealed class FeatureOverviewEndpointsLog;

    public static void MapFeatureOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Features")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleGetFeatureOverview)
            .WithDisplayName("Get Feature Overview")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<FeatureOverviewResponse>>();
    }

    private static IResult HandleGetFeatureOverview(
        [FromServices] ILicenseStatusProvider licenseProvider,
        [FromServices] ILogger<FeatureOverviewEndpointsLog> logger)
    {
        AdminLog.FeatureOverviewQueried(logger);

        var status = licenseProvider.GetCurrentStatus();

        var features = FeatureCatalog.All.Select(f =>
        {
            var isEnabled = status.Edition >= f.MinimumEdition;
            return new FeatureOverviewItem
            {
                Key = f.Key,
                DisplayName = f.DisplayName,
                Category = f.Category,
                Description = f.Description,
                IsEnabled = isEnabled,
                MinimumEdition = f.MinimumEdition.ToString(),
                UpgradeMessage = isEnabled ? null : $"Requires {f.MinimumEdition}"
            };
        }).ToArray();

        var response = new FeatureOverviewResponse
        {
            CurrentEdition = status.Edition.ToString(),
            Features = features
        };

        return Results.Json(
            ApiResponse<FeatureOverviewResponse>.CreateSuccess(response),
            FeatureOverviewJsonContext.Default.ApiResponseFeatureOverviewResponse);
    }
}
