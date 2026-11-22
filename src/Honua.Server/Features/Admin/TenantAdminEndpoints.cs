// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.MultiTenancy.Billing;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.MultiTenancy.Lifecycle;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static partial class TenantAdminEndpoints
{
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

    public static void MapTenantAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/tenants")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Tenants")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority()
            .AddEndpointFilter((invocationContext, next) =>
            {
                var context = invocationContext.HttpContext;
                var gate = LicenseGate.RequireEntitlement(
                    context,
                    FeatureCatalog.MultiTenancyKey,
                    "Multi-tenant operation");

                return gate is null
                    ? next(invocationContext)
                    : ValueTask.FromResult<object?>(gate);
            });

        var list = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.list", "/tenants");
        group.MapMethods(list.RoutePattern, list.Methods, HandleListTenants)
            .WithAdminOperation(list.Definition).WithDisplayName("List Tenants");

        var create = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.create", "/tenants");
        group.MapMethods(create.RoutePattern, create.Methods, HandleCreateTenant)
            .WithAdminOperation(create.Definition).WithDisplayName("Create Tenant");

        var usage = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.usage.export", "/tenants");
        group.MapMethods(usage.RoutePattern, usage.Methods, HandleUsage)
            .WithAdminOperation(usage.Definition).WithDisplayName("Export Tenant Billing Usage");

        var get = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.get", "/tenants");
        group.MapMethods(get.RoutePattern, get.Methods, HandleGetTenant)
            .WithAdminOperation(get.Definition).WithDisplayName("Get Tenant");

        var suspend = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.suspend", "/tenants");
        group.MapMethods(suspend.RoutePattern, suspend.Methods, HandleSuspendTenant)
            .WithAdminOperation(suspend.Definition).WithDisplayName("Suspend Tenant");

        var resume = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.resume", "/tenants");
        group.MapMethods(resume.RoutePattern, resume.Methods, HandleResumeTenant)
            .WithAdminOperation(resume.Definition).WithDisplayName("Resume Tenant");

        var delete = AdminHttpOperationRouteBinding.ForAccess("admin.tenant.delete", "/tenants");
        group.MapMethods(delete.RoutePattern, delete.Methods, HandleDeleteTenant)
            .WithAdminOperation(delete.Definition).WithDisplayName("Delete Tenant");
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
        // Intentional catch-all request-handling boundary: logs and returns a generic
        // 500 problem response for the tenant listing endpoint.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            if (result.Outcome == TenantLifecycleOutcome.Invalid)
            {
                return MapFailure(result);
            }

            Log.TenantOperation(logger, request.TenantId, "create", result.Outcome);
            if (!result.Success)
            {
                return MapFailure(result);
            }

            return TypedResults.Created(
                $"/api/v1/admin/tenants/{result.Tenant!.TenantId}",
                ApiResponse<TenantResponse>.CreateSuccess(ToResponse(result.Tenant)));
        }
        // Intentional catch-all request-handling boundary: logs and returns a generic
        // 500 problem response for the tenant creation endpoint.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: logs and returns a generic
        // 500 problem response for the tenant retrieval endpoint.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            if (result.Outcome == TenantLifecycleOutcome.Invalid)
            {
                return MapFailure(result);
            }

            Log.TenantOperation(logger, tenantId, operation, result.Outcome);
            if (!result.Success)
            {
                return MapFailure(result);
            }

            return TypedResults.Ok(ApiResponse<TenantResponse>.CreateSuccess(ToResponse(result.Tenant!)));
        }
        // Intentional catch-all request-handling boundary: shared handler for the
        // suspend/resume/delete tenant lifecycle endpoints; logs and returns a
        // generic 500 problem response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentional catch-all request-handling boundary: logs and returns a generic
        // 500 problem response for the tenant billing usage export endpoint.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.TenantOperationFailed(logger, "usage", ex);
            return TypedResults.Problem(
                title: "Tenant usage export failed",
                detail: "An internal error occurred while exporting tenant usage.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
