// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for license management.
/// </summary>
internal static partial class LicenseEndpoints
{
    /// <summary>
    /// Log category for license endpoints.
    /// </summary>
    internal sealed class LicenseEndpointsLog;

    /// <summary>
    /// Source-generated logging methods for license endpoints.
    /// </summary>
    internal static partial class LicenseLog
    {
        [LoggerMessage(EventId = 4550, Level = LogLevel.Information,
            Message = "License status queried: Edition={Edition}, Valid={IsValid}")]
        public static partial void LicenseStatusQueried(ILogger logger, string edition, bool isValid);

        [LoggerMessage(EventId = 4551, Level = LogLevel.Information,
            Message = "License uploaded successfully: Edition={Edition}")]
        public static partial void LicenseUploaded(ILogger logger, string edition);

        [LoggerMessage(EventId = 4552, Level = LogLevel.Warning,
            Message = "License upload failed: {Error}")]
        public static partial void LicenseUploadFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 4553, Level = LogLevel.Information,
            Message = "Entitlements queried: {Count} entitlements")]
        public static partial void EntitlementsQueried(ILogger logger, int count);
    }

    /// <summary>
    /// Configure license admin endpoints with formal API versioning.
    /// </summary>
    public static void MapLicenseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/license")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "License")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleGetLicenseStatus)
            .WithDisplayName("Get License Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleUploadLicense)
            .WithDisplayName("Upload License")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/entitlements", HandleGetEntitlements)
            .WithDisplayName("Get Entitlements")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Results<Ok<ApiResponse<LicenseStatusResponse>>, ProblemHttpResult>>
        HandleGetLicenseStatus(
            [FromServices] ILicenseManager licenseManager,
            [FromServices] ILogger<LicenseEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var info = await licenseManager.GetLicenseInfoAsync(context.RequestAborted);

            var response = new LicenseStatusResponse
            {
                Edition = info.Edition,
                ExpiresAt = info.ExpiresAt,
                IsValid = info.IsValid,
                ValidationState = info.ValidationState,
                LicensedTo = info.LicensedTo,
                IssuedAt = info.IssuedAt,
                Entitlements = info.Entitlements.Select(e => new EntitlementResponse
                {
                    Key = e.Key,
                    Name = e.Name,
                    IsActive = e.IsActive,
                }).ToList(),
            };

            LicenseLog.LicenseStatusQueried(logger, info.Edition, info.IsValid);
            return TypedResults.Ok(ApiResponse<LicenseStatusResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve license status");
            return TypedResults.Problem(
                title: "License status retrieval failed",
                detail: "An internal error occurred while retrieving license status.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<LicenseStatusResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUploadLicense(
            [FromServices] ILicenseManager licenseManager,
            [FromServices] ILogger<LicenseEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
            var licenseData = ms.ToArray();

            if (licenseData.Length == 0)
            {
                LicenseLog.LicenseUploadFailed(logger, "Empty license data");
                return TypedResults.BadRequest(ApiResponse<object>.Failure("License data cannot be empty"));
            }

            var info = await licenseManager.ApplyLicenseAsync(licenseData, context.RequestAborted);

            var response = new LicenseStatusResponse
            {
                Edition = info.Edition,
                ExpiresAt = info.ExpiresAt,
                IsValid = info.IsValid,
                ValidationState = info.ValidationState,
                LicensedTo = info.LicensedTo,
                IssuedAt = info.IssuedAt,
                Entitlements = info.Entitlements.Select(e => new EntitlementResponse
                {
                    Key = e.Key,
                    Name = e.Name,
                    IsActive = e.IsActive,
                }).ToList(),
            };

            LicenseLog.LicenseUploaded(logger, info.Edition);
            return TypedResults.Ok(ApiResponse<LicenseStatusResponse>.CreateSuccess(response));
        }
        catch (ArgumentException ex)
        {
            LicenseLog.LicenseUploadFailed(logger, ex.Message);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("License data is invalid."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload license");
            return TypedResults.Problem(
                title: "License upload failed",
                detail: "An internal error occurred while uploading the license.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<EntitlementResponse>>>, ProblemHttpResult>>
        HandleGetEntitlements(
            [FromServices] ILicenseManager licenseManager,
            [FromServices] ILogger<LicenseEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var entitlements = await licenseManager.GetEntitlementsAsync(context.RequestAborted);

            var response = entitlements.Select(e => new EntitlementResponse
            {
                Key = e.Key,
                Name = e.Name,
                IsActive = e.IsActive,
            }).ToList();

            LicenseLog.EntitlementsQueried(logger, response.Count);
            IReadOnlyList<EntitlementResponse> readOnlyResponse = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<EntitlementResponse>>.CreateSuccess(readOnlyResponse));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve entitlements");
            return TypedResults.Problem(
                title: "Entitlement retrieval failed",
                detail: "An internal error occurred while retrieving entitlements.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
