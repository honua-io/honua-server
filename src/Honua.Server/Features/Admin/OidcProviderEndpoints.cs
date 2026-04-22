// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for OIDC identity provider configuration.
/// </summary>
internal static partial class OidcProviderEndpoints
{
    /// <summary>
    /// Log category for OIDC provider endpoints.
    /// </summary>
    internal sealed class OidcProviderEndpointsLog;

    internal static partial class OidcProviderLog
    {
        [LoggerMessage(EventId = 4560, Level = LogLevel.Information,
            Message = "Listed {Count} OIDC providers")]
        public static partial void ProvidersListed(ILogger logger, int count);

        [LoggerMessage(EventId = 4561, Level = LogLevel.Information,
            Message = "Created OIDC provider '{Name}' with ID {ProviderId}")]
        public static partial void ProviderCreated(ILogger logger, string name, Guid providerId);

        [LoggerMessage(EventId = 4562, Level = LogLevel.Information,
            Message = "Deleted OIDC provider {ProviderId}")]
        public static partial void ProviderDeleted(ILogger logger, Guid providerId);

        [LoggerMessage(EventId = 4563, Level = LogLevel.Information,
            Message = "Tested OIDC provider {ProviderId}: Reachable={IsReachable}")]
        public static partial void ProviderTested(ILogger logger, Guid providerId, bool isReachable);
    }

    /// <summary>
    /// Configure OIDC provider admin endpoints.
    /// </summary>
    public static void MapOidcProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/oidc/providers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "OIDC")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListProviders)
            .WithDisplayName("List OIDC Providers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateProvider)
            .WithDisplayName("Create OIDC Provider")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}", HandleGetProvider)
            .WithDisplayName("Get OIDC Provider")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id:guid}", HandleUpdateProvider)
            .WithDisplayName("Update OIDC Provider")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id:guid}", HandleDeleteProvider)
            .WithDisplayName("Delete OIDC Provider")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPost("/{id:guid}/test", HandleTestProvider)
            .WithDisplayName("Test OIDC Provider Connection")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static OidcProviderResponse ToResponse(OidcProviderConfiguration p) => new()
    {
        ProviderId = p.ProviderId,
        Name = p.Name,
        ProviderType = p.ProviderType,
        Authority = p.Authority,
        ClientId = p.ClientId,
        Enabled = p.Enabled,
        IsHealthy = p.IsHealthy,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        LastHealthCheck = p.LastHealthCheck,
    };

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<OidcProviderResponse>>>, ProblemHttpResult>>
        HandleListProviders(
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var providers = await store.ListProvidersAsync(context.RequestAborted);
            var response = providers.Select(ToResponse).ToList();
            OidcProviderLog.ProvidersListed(logger, response.Count);
            IReadOnlyList<OidcProviderResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<OidcProviderResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex)
        {
            OidcProviderLog.ListProvidersFailed(logger, ex);
            return TypedResults.Problem(
                title: "OIDC provider listing failed",
                detail: "An internal error occurred while listing OIDC providers.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<OidcProviderResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreateProvider(
            CreateOidcProviderRequest request,
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
            {
                var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {errors}"));
            }

            var provider = new OidcProviderConfiguration
            {
                Name = request.Name,
                ProviderType = request.ProviderType,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret,
                Enabled = request.Enabled,
            };

            var created = await store.CreateProviderAsync(provider, context.RequestAborted);
            var response = ToResponse(created);

            OidcProviderLog.ProviderCreated(logger, created.Name, created.ProviderId);
            return TypedResults.Created($"/api/v1/admin/oidc/providers/{created.ProviderId}",
                ApiResponse<OidcProviderResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            OidcProviderLog.CreateProviderFailed(logger, ex);
            return TypedResults.Problem(
                title: "OIDC provider creation failed",
                detail: "An internal error occurred while creating the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<OidcProviderResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetProvider(
            Guid id,
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var provider = await store.GetProviderAsync(id, context.RequestAborted);
            if (provider == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("OIDC provider not found"));
            }

            return TypedResults.Ok(ApiResponse<OidcProviderResponse>.CreateSuccess(ToResponse(provider)));
        }
        catch (Exception ex)
        {
            OidcProviderLog.GetProviderFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "OIDC provider retrieval failed",
                detail: "An internal error occurred while retrieving the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<OidcProviderResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateProvider(
            Guid id,
            UpdateOidcProviderRequest request,
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var existing = await store.GetProviderAsync(id, context.RequestAborted);
            if (existing == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("OIDC provider not found"));
            }

            var updated = new OidcProviderConfiguration
            {
                ProviderId = existing.ProviderId,
                Name = request.Name ?? existing.Name,
                ProviderType = existing.ProviderType,
                Authority = request.Authority ?? existing.Authority,
                ClientId = request.ClientId ?? existing.ClientId,
                ClientSecret = request.ClientSecret ?? existing.ClientSecret,
                Enabled = request.Enabled ?? existing.Enabled,
                IsHealthy = existing.IsHealthy,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastHealthCheck = existing.LastHealthCheck,
            };

            var result = await store.UpdateProviderAsync(updated, context.RequestAborted);
            return TypedResults.Ok(ApiResponse<OidcProviderResponse>.CreateSuccess(ToResponse(result!)));
        }
        catch (Exception ex)
        {
            OidcProviderLog.UpdateProviderFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "OIDC provider update failed",
                detail: "An internal error occurred while updating the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeleteProvider(
            Guid id,
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeleteProviderAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("OIDC provider not found"));
            }

            OidcProviderLog.ProviderDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("OIDC provider deleted"));
        }
        catch (Exception ex)
        {
            OidcProviderLog.DeleteProviderFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "OIDC provider deletion failed",
                detail: "An internal error occurred while deleting the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<OidcProviderTestResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleTestProvider(
            Guid id,
            [FromServices] IOidcProviderStore store,
            [FromServices] ILogger<OidcProviderEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var provider = await store.GetProviderAsync(id, context.RequestAborted);
            if (provider == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("OIDC provider not found"));
            }

            var testResult = await store.TestProviderAsync(id, context.RequestAborted);

            var response = new OidcProviderTestResponse
            {
                ProviderId = id,
                IsReachable = testResult.IsReachable,
                Message = testResult.Message,
                TestedAt = testResult.TestedAt,
            };

            OidcProviderLog.ProviderTested(logger, id, testResult.IsReachable);
            return TypedResults.Ok(ApiResponse<OidcProviderTestResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            OidcProviderLog.TestProviderFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "OIDC provider test failed",
                detail: "An internal error occurred while testing the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
