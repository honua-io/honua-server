// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Billing;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.MultiTenancy.Lifecycle;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for tenant lifecycle/provisioning and per-tenant billing usage (issue #2156).
/// </summary>
internal static partial class TenantAdminEndpoints
{
    /// <summary>Log category for tenant admin endpoints.</summary>
    internal sealed class TenantAdminLog;

    internal static partial class Log
    {
        [LoggerMessage(EventId = 4561, Level = LogLevel.Information,
            Message = "Tenant '{TenantId}' lifecycle operation '{Operation}' produced outcome {Outcome}")]
        public static partial void TenantOperation(ILogger logger, string tenantId, string operation, TenantLifecycleOutcome outcome);

        [LoggerMessage(EventId = 4562, Level = LogLevel.Error,
            Message = "Tenant lifecycle operation '{Operation}' failed")]
        public static partial void TenantOperationFailed(ILogger logger, string operation, Exception exception);
    }

    /// <summary>Configure tenant lifecycle admin endpoints.</summary>
    /// <param name="endpoints">The route builder.</param>
    public static void MapTenantAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/tenants")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Tenants")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListTenants)
            .WithDisplayName("List Tenants")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateTenant)
            .WithDisplayName("Create Tenant")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/usage", HandleUsage)
            .WithDisplayName("Export Tenant Billing Usage")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{tenantId}", HandleGetTenant)
            .WithDisplayName("Get Tenant")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/{tenantId}/suspend", HandleSuspendTenant)
            .WithDisplayName("Suspend Tenant")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{tenantId}/resume", HandleResumeTenant)
            .WithDisplayName("Resume Tenant")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/{tenantId}", HandleDeleteTenant)
            .WithDisplayName("Delete Tenant")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static TenantResponse ToResponse(TenantRecord t) => new()
    {
        TenantId = t.TenantId,
        DisplayName = t.DisplayName,
        Status = t.Status.ToString(),
        Plan = t.Plan,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        SuspendedAt = t.SuspendedAt,
        DeletedAt = t.DeletedAt,
    };

    private static TenantLifecycleActor BuildActor(HttpContext context)
    {
        var identity = context.User?.Identity;
        var actor = identity?.IsAuthenticated == true && !string.IsNullOrEmpty(identity.Name)
            ? identity.Name!
            : AuditEvent.AnonymousActor;
        var actorType = identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous;

        var correlationId = context.Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        return new TenantLifecycleActor(
            actor,
            actorType,
            correlationId,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null);
    }

    private static IResult MapFailure(TenantLifecycleResult result) => result.Outcome switch
    {
        TenantLifecycleOutcome.NotFound => TypedResults.NotFound(ApiResponse<object>.Failure(result.Error ?? "Tenant not found")),
        TenantLifecycleOutcome.Conflict => TypedResults.Conflict(ApiResponse<object>.Failure(result.Error ?? "Tenant already exists")),
        TenantLifecycleOutcome.InvalidTransition => TypedResults.Conflict(ApiResponse<object>.Failure(result.Error ?? "Invalid tenant state transition")),
        _ => TypedResults.BadRequest(ApiResponse<object>.Failure(result.Error ?? "Invalid request")),
    };

    private static async Task<IResult> HandleListTenants(
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
    {
        try
        {
            var tenants = await service.ListAsync(context.RequestAborted);
            IReadOnlyList<TenantResponse> response = tenants.Select(ToResponse).ToList().AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<TenantResponse>>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            Log.TenantOperationFailed(logger, "list", ex);
            return TypedResults.Problem(
                title: "Tenant listing failed",
                detail: "An internal error occurred while listing tenants.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleCreateTenant(
        CreateTenantRequest request,
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
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

            var result = await service.CreateAsync(
                request.TenantId,
                request.DisplayName ?? request.TenantId,
                request.Plan,
                BuildActor(context),
                context.RequestAborted);

            Log.TenantOperation(logger, request.TenantId, "create", result.Outcome);
            if (!result.Success)
            {
                return MapFailure(result);
            }

            return TypedResults.Created(
                $"/api/v1/admin/tenants/{result.Tenant!.TenantId}",
                ApiResponse<TenantResponse>.CreateSuccess(ToResponse(result.Tenant)));
        }
        catch (Exception ex)
        {
            Log.TenantOperationFailed(logger, "create", ex);
            return TypedResults.Problem(
                title: "Tenant creation failed",
                detail: "An internal error occurred while creating the tenant.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleGetTenant(
        string tenantId,
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
    {
        try
        {
            var tenant = await service.GetAsync(tenantId, context.RequestAborted);
            if (tenant is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Tenant not found"));
            }

            return TypedResults.Ok(ApiResponse<TenantResponse>.CreateSuccess(ToResponse(tenant)));
        }
        catch (Exception ex)
        {
            Log.TenantOperationFailed(logger, "get", ex);
            return TypedResults.Problem(
                title: "Tenant retrieval failed",
                detail: "An internal error occurred while retrieving the tenant.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<IResult> HandleSuspendTenant(
        string tenantId,
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
        => HandleTransition(tenantId, "suspend", service.SuspendAsync, logger, context);

    private static Task<IResult> HandleResumeTenant(
        string tenantId,
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
        => HandleTransition(tenantId, "resume", service.ResumeAsync, logger, context);

    private static Task<IResult> HandleDeleteTenant(
        string tenantId,
        [FromServices] TenantLifecycleService service,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
        => HandleTransition(tenantId, "delete", service.DeleteAsync, logger, context);

    private static async Task<IResult> HandleTransition(
        string tenantId,
        string operation,
        Func<string, TenantLifecycleActor, CancellationToken, Task<TenantLifecycleResult>> transition,
        ILogger<TenantAdminLog> logger,
        HttpContext context)
    {
        try
        {
            var result = await transition(tenantId, BuildActor(context), context.RequestAborted);
            Log.TenantOperation(logger, tenantId, operation, result.Outcome);
            if (!result.Success)
            {
                return MapFailure(result);
            }

            return TypedResults.Ok(ApiResponse<TenantResponse>.CreateSuccess(ToResponse(result.Tenant!)));
        }
        catch (Exception ex)
        {
            Log.TenantOperationFailed(logger, operation, ex);
            return TypedResults.Problem(
                title: "Tenant lifecycle operation failed",
                detail: "An internal error occurred while updating the tenant.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleUsage(
        [FromServices] TenantBillingExporter exporter,
        [FromServices] ILogger<TenantAdminLog> logger,
        HttpContext context)
    {
        try
        {
            var records = await exporter.ExportAsync(context.RequestAborted);
            IReadOnlyList<TenantUsageRecordResponse> mapped = records
                .Select(r => new TenantUsageRecordResponse
                {
                    TenantId = r.TenantId,
                    RequestCount = r.RequestCount,
                    CapturedAt = r.CapturedAt,
                })
                .ToList()
                .AsReadOnly();

            var response = new TenantUsageResponse { Records = mapped };
            return TypedResults.Ok(ApiResponse<TenantUsageResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            Log.TenantOperationFailed(logger, "usage", ex);
            return TypedResults.Problem(
                title: "Tenant usage export failed",
                detail: "An internal error occurred while exporting tenant usage.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
