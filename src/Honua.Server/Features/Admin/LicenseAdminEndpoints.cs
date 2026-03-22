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
/// Admin endpoints for license management and entitlements.
/// </summary>
internal static class LicenseAdminEndpoints
{
    internal sealed class LicenseAdminEndpointsLog;

    public static void MapLicenseAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/license")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "License")
            .RequireAdminAuthorization();

        group.MapGet("/status", HandleGetLicenseStatus)
            .WithDisplayName("Get License Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<LicenseStatusResponse>>();

        group.MapGet("/features", HandleGetEntitlements)
            .WithDisplayName("Get License Feature Entitlements")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<LicenseEntitlementsResponse>>();

        group.MapPost("/upload", HandleUploadLicense)
            .WithDisplayName("Upload License File")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<LicenseUploadResponse>>(StatusCodes.Status501NotImplemented);
    }

    private static IResult HandleGetLicenseStatus(
        [FromServices] ILicenseStatusProvider licenseProvider,
        [FromServices] ILogger<LicenseAdminEndpointsLog> logger)
    {
        AdminLog.LicenseStatusQueried(logger);

        var status = licenseProvider.GetCurrentStatus();

        var response = new LicenseStatusResponse
        {
            Edition = status.Edition.ToString(),
            IsValid = status.IsValid,
            ExpiresAt = status.ExpiresAt,
            LicensedTo = status.LicensedTo,
            ValidationState = status.IsValid ? "valid" : "invalid",
            DaysUntilExpiry = status.DaysUntilExpiry,
            ExpiryWarning = status.DaysUntilExpiry is <= 30
        };

        return Results.Json(
            ApiResponse<LicenseStatusResponse>.CreateSuccess(response),
            LicenseAdminJsonContext.Default.ApiResponseLicenseStatusResponse);
    }

    private static IResult HandleGetEntitlements(
        [FromServices] ILicenseStatusProvider licenseProvider,
        [FromServices] ILogger<LicenseAdminEndpointsLog> logger)
    {
        AdminLog.LicenseEntitlementsQueried(logger);

        var status = licenseProvider.GetCurrentStatus();
        var features = FeatureCatalog.All.Select(f => new FeatureEntitlementItem
        {
            Key = f.Key,
            DisplayName = f.DisplayName,
            Category = f.Category,
            IsEnabled = status.Edition >= f.MinimumEdition,
            MinimumEdition = f.MinimumEdition.ToString(),
            UpgradeRequired = status.Edition < f.MinimumEdition
        }).ToArray();

        var response = new LicenseEntitlementsResponse
        {
            Edition = status.Edition.ToString(),
            Features = features
        };

        return Results.Json(
            ApiResponse<LicenseEntitlementsResponse>.CreateSuccess(response),
            LicenseAdminJsonContext.Default.ApiResponseLicenseEntitlementsResponse);
    }

    private static async Task<IResult> HandleUploadLicense(
        [FromServices] ILicenseStatusProvider licenseProvider,
        [FromServices] ILogger<LicenseAdminEndpointsLog> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AdminLog.LicenseUploadAttempted(logger);

        var result = await licenseProvider.UploadLicenseAsync(
            context.Request.Body, cancellationToken).ConfigureAwait(false);

        var response = new LicenseUploadResponse
        {
            Success = result.Success,
            Message = result.Message
        };

        if (!result.Success)
        {
            return Results.Json(
                new ApiResponse<LicenseUploadResponse> { Success = false, Data = response },
                LicenseAdminJsonContext.Default.ApiResponseLicenseUploadResponse,
                statusCode: StatusCodes.Status501NotImplemented);
        }

        return Results.Json(
            ApiResponse<LicenseUploadResponse>.CreateSuccess(response),
            LicenseAdminJsonContext.Default.ApiResponseLicenseUploadResponse);
    }
}
