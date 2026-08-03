// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
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
            .RequireAdminAuthorization()
            // #2997: OIDC provider configuration is the Pro identity.oidc surface (ADR-0024
            // Identity tier — "no SSO tax for one provider"), mirroring the #2978 SAML/SCIM
            // gate shape. Configuring a second provider additionally requires the Enterprise
            // identity.oidc-multi-provider entitlement; that check runs first for creates that
            // would grow the store beyond one provider so the 402 names the entitlement that
            // is actually being exceeded. The JWT bearer / token-validation pipeline
            // (OidcAuthenticationExtensions) is deliberately NOT gated: token validation for
            // already-configured providers keeps working regardless of edition.
            .AddEndpointFilter(ApplyProviderEntitlementGatesAsync);

        group.MapGet("/", HandleListProviders)
            .WithDisplayName("List OIDC Providers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateProvider)
            .WithDisplayName("Create OIDC Provider")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .WithMetadata(CreateProviderRoute.Instance);

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

    /// <summary>
    /// Marker metadata identifying the provider-create route (<c>POST …/providers</c>). The
    /// entitlement filter tells the create route apart from the connectivity-test route
    /// (<c>POST …/providers/{id}/test</c>) from the matched endpoint's metadata instead of the
    /// request path: the previous "path does not end in <c>/test</c>" check classified the equally
    /// valid <c>…/providers/{id}/test/</c> form (trailing slash, which routing still matches) as a
    /// create and rejected a connectivity test with the multi-provider 402.
    /// </summary>
    internal sealed class CreateProviderRoute
    {
        internal static readonly CreateProviderRoute Instance = new();
    }

    /// <summary>
    /// Serializes provider creation so the provider-count check and the store mutation form one
    /// critical section. Two concurrent Pro creates could otherwise both observe an empty store,
    /// both pass the preflight, and both be accepted with distinct generated IDs — silently
    /// bypassing the Enterprise identity.oidc-multi-provider entitlement. The lock is
    /// process-wide, which matches the scope of the current in-memory store; when the persistent
    /// store lands (#496) the invariant must move into the store's own transaction so it holds
    /// across replicas too.
    /// </summary>
    private static readonly SemaphoreSlim ProviderCreationGate = new(1, 1);

    /// <summary>
    /// #2997: applies the OIDC provider admin entitlement gates. Reads and non-create mutations
    /// only need the Pro <c>identity.oidc</c> entitlement; a create that would grow the store past
    /// a single provider needs the Enterprise <c>identity.oidc-multi-provider</c> entitlement,
    /// checked first so the 402 names the entitlement actually being exceeded and evaluated inside
    /// <see cref="ProviderCreationGate"/> so it cannot be raced by a concurrent create.
    /// </summary>
    internal static async ValueTask<object?> ApplyProviderEntitlementGatesAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var httpContext = invocationContext.HttpContext;
        var isCreate = httpContext.GetEndpoint()?.Metadata.GetMetadata<CreateProviderRoute>() is not null;
        if (!isCreate)
        {
            var readGate = RequireOidcAuthenticationEntitlement(httpContext);
            return readGate is null ? await next(invocationContext).ConfigureAwait(false) : readGate;
        }

        await ProviderCreationGate.WaitAsync(httpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var store = httpContext.RequestServices.GetRequiredService<IOidcProviderStore>();
            var existing = await store.ListProvidersAsync(httpContext.RequestAborted).ConfigureAwait(false);
            if (existing.Count >= 1)
            {
                var multiProviderGate = LicenseGate.RequireEntitlement(
                    httpContext,
                    FeatureCatalog.OidcMultiProviderKey,
                    "OIDC multi-provider SSO");
                if (multiProviderGate is not null)
                {
                    return multiProviderGate;
                }
            }

            var gate = RequireOidcAuthenticationEntitlement(httpContext);
            return gate is null ? await next(invocationContext).ConfigureAwait(false) : gate;
        }
        finally
        {
            ProviderCreationGate.Release();
        }
    }

    private static IResult? RequireOidcAuthenticationEntitlement(HttpContext context)
        => LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.OidcAuthenticationKey,
            "OIDC authentication");

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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider listing endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider creation endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider retrieval endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider update endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider deletion endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: this is the OIDC
        // provider connectivity-test endpoint; the failure is logged and mapped
        // to a generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OidcProviderLog.TestProviderFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "OIDC provider test failed",
                detail: "An internal error occurred while testing the OIDC provider.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
