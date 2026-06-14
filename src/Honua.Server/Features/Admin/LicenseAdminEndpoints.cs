// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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

        group.MapGet("/capacity", HandleGetCapacityState)
            .WithDisplayName("Get License Capacity State")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<LicenseCapacityState>>();

        group.MapPost("/capacity/surge", HandleSetSurgeMode)
            .WithDisplayName("Set License Surge Mode")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<LicenseCapacityState>>()
            .Produces<ApiResponse<object>>(StatusCodes.Status400BadRequest);

        group.MapGet("/features", HandleGetEntitlements)
            .WithDisplayName("Get License Feature Entitlements")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<LicenseEntitlementsResponse>>();

        group.MapPost("/upload", HandleUploadLicense)
            .WithDisplayName("Upload License File")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<LicenseUploadResponse>>()
            .Produces<ApiResponse<LicenseUploadResponse>>(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandleGetLicenseStatus(
        [FromServices] ILicenseEntitlementService entitlementService,
        [FromServices] ILicenseCapacityMeter capacityMeter,
        [FromServices] IOptions<LicenseOptions> licenseOptions,
        [FromServices] ILogger<LicenseAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        AdminLog.LicenseStatusQueried(logger);

        var snapshot = entitlementService.GetSnapshot();
        var status = new LicenseStatus(
            snapshot.Edition,
            snapshot.IsValid,
            snapshot.ExpiresAt,
            snapshot.LicensedTo,
            snapshot.ValidationState,
            snapshot.LicenseId,
            snapshot.IssuedAt,
            snapshot.Entitlements,
            snapshot.CapacityTerms);

        var capacity = await capacityMeter.GetCapacityStateAsync(cancellationToken).ConfigureAwait(false);
        var response = LicenseStatusResponseMapper.FromStatus(
            status,
            licenseOptions.Value.ExpiryWarningDays,
            capacity);

        return Results.Json(
            ApiResponse<LicenseStatusResponse>.CreateSuccess(response),
            LicenseAdminJsonContext.Default.ApiResponseLicenseStatusResponse);
    }

    private static async Task<IResult> HandleGetCapacityState(
        [FromServices] ILicenseCapacityMeter capacityMeter,
        CancellationToken cancellationToken)
    {
        var capacity = await capacityMeter.GetCapacityStateAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LicenseCapacityState>.CreateSuccess(capacity),
            LicenseAdminJsonContext.Default.ApiResponseLicenseCapacityState);
    }

    private static async Task<IResult> HandleSetSurgeMode(
        [FromServices] ILicenseCapacityMeter capacityMeter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var request = await context.Request.ReadFromJsonAsync(
            LicenseAdminJsonContext.Default.LicenseSurgeModeRequest,
            cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Results.Json(
                ApiResponse<object>.Failure("Surge mode request body is required."),
                LicenseAdminJsonContext.Default.ApiResponseObject,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var capacity = await capacityMeter
            .SetSurgeModeAsync(request.Enabled, request.Reason, cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LicenseCapacityState>.CreateSuccess(capacity),
            LicenseAdminJsonContext.Default.ApiResponseLicenseCapacityState);
    }

    private static IResult HandleGetEntitlements(
        [FromServices] ILicenseEntitlementService entitlementService,
        [FromServices] ILogger<LicenseAdminEndpointsLog> logger)
    {
        AdminLog.LicenseEntitlementsQueried(logger);

        var snapshot = entitlementService.GetSnapshot();
        var features = FeatureCatalog.All.Select(f => new FeatureEntitlementItem
        {
            Key = f.Key,
            DisplayName = f.DisplayName,
            Category = f.Category,
            IsEnabled = snapshot.HasEntitlement(f.Key),
            MinimumEdition = f.MinimumEdition.ToString(),
            UpgradeRequired = !snapshot.HasEntitlement(f.Key)
        }).ToArray();

        var response = new LicenseEntitlementsResponse
        {
            Edition = snapshot.Edition.ToString(),
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
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(
            ApiResponse<LicenseUploadResponse>.CreateSuccess(response),
            LicenseAdminJsonContext.Default.ApiResponseLicenseUploadResponse);
    }
}
