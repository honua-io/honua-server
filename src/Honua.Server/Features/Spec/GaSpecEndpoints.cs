// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Workflow;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Honua.Server.Features.Spec;

internal static class GaSpecEndpoints
{
    internal static void MapGaSpecEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>("GaSpec:Enabled"))
        {
            return;
        }

        var group = endpoints.MapGroup("/v1/spec/ga").WithTags("GA Spec").RequireAdminAuthorization();
        group.MapGet("/bundle", () =>
        {
            var bundle = WorkflowBundle.LoadDefault();
            return System.Text.Encoding.UTF8.GetByteCount(bundle.Json) <= 262144
                ? Results.Content(bundle.Json, "application/json")
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }).WithName("GaSpecBundle");
        group.MapPost("/plans", PlanAsync).WithName("GaSpecPlan");
        group.MapGet("/plans/{id}", ReadPlanAsync).WithName("GaSpecReadPlan");
        group.MapPost("/plans/{id}/apply", ApplyAsync).WithName("GaSpecHumanApply");
        group.MapGet("/runs/{id}", ReadRunAsync).WithName("GaSpecReadRun");
    }

    private static async Task<IResult> PlanAsync(HttpContext context, GaSpecReviewService service,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            var spec = await JsonSerializer.DeserializeAsync(context.Request.Body,
                GaSpecJsonContext.Default.SpecLayerIntent, cancellationToken).ConfigureAwait(false)
                ?? throw new GaSpecBlockedException("invalid-intent");
            var operation = await service.PlanAsync(spec, Tenant(tenant), Actor(context), cancellationToken).ConfigureAwait(false);
            return View(operation);
        }).ConfigureAwait(false);
    }

    private static Task<IResult> ReadPlanAsync(string id, string? revision, HttpContext context, GaSpecReviewService service,
        ITenantContext tenant, CancellationToken cancellationToken) => GuardAsync(async () =>
        {
            var operation = await service.GetAsync(id, Tenant(tenant), cancellationToken).ConfigureAwait(false);
            if (operation is null) return Results.NotFound();
            await service.AuthorizeReadAsync(operation, Tenant(tenant), Actor(context), cancellationToken).ConfigureAwait(false);
            if (revision != operation.SpecReview!.Revision) throw new GaSpecBlockedException("review-stale");
            return View(operation);
        });

    private static Task<IResult> ReadRunAsync(string id, HttpContext context, GaSpecReviewService service,
        ITenantContext tenant, CancellationToken cancellationToken) => GuardAsync(async () =>
        {
            var operation = await service.GetAsync(id, Tenant(tenant), cancellationToken).ConfigureAwait(false);
            if (operation?.SpecReview?.CommandId is null) return Results.NotFound();
            await service.AuthorizeReadAsync(operation, Tenant(tenant), Actor(context), cancellationToken).ConfigureAwait(false);
            return View(operation);
        });

    private static async Task<IResult> ApplyAsync(string id, HttpContext context, GaSpecReviewService service,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        // Explicit concrete handler authentication prevents an API key, arbitrary
        // IdP claim or model-facing operator bearer from claiming human authority.
        var session = await context.AuthenticateAsync(OidcAuthenticationExtensions.AdminSessionScheme).ConfigureAwait(false);
        if (!session.Succeeded || session.Principal is null || context.User.Identities.Any(identity =>
                identity.IsAuthenticated && identity.AuthenticationType != OidcAuthenticationExtensions.AdminSessionScheme))
            return Problem("human-session-required", StatusCodes.Status403Forbidden);
        // Re-evaluate using only the human principal: a more privileged bearer or
        // API key in the same request cannot lend grants to a low-privilege cookie.
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(session.Principal, context, AuthenticationExtensions.AdminPolicy).ConfigureAwait(false)).Succeeded)
            return Problem("human-permission-denied", StatusCodes.Status403Forbidden);
        context.User = session.Principal;
        var entitlement = LicenseGate.RequireEntitlement(context, FeatureCatalog.AiSpecApplyKey, "GA spec apply");
        if (entitlement is not null) return entitlement;
        return await GuardAsync(async () =>
        {
            var command = await JsonSerializer.DeserializeAsync(context.Request.Body,
                GaSpecJsonContext.Default.GaSpecApplyRequest, cancellationToken).ConfigureAwait(false)
                ?? throw new GaSpecBlockedException("invalid-command");
            var operation = await service.ApplyAsync(id, command, Tenant(tenant), Actor(context), cancellationToken).ConfigureAwait(false);
            return View(operation);
        }).ConfigureAwait(false);
    }

    private static string Actor(HttpContext context) => CanonicalSecurityActor.Resolve(context.User)?.ActorId
        ?? throw new GaSpecBlockedException("durable-actor-required");
    private static string Tenant(ITenantContext tenant) => tenant.RequireTenantId(out var id, out _)
        ? id : throw new GaSpecBlockedException("tenant-required");
    private static IResult View(WorkflowOperationRecord operation) => Results.Json(
        GaSpecReviewService.ToView(operation), GaSpecJsonContext.Default.GaSpecView);
    private static IResult Problem(string code, int status = StatusCodes.Status409Conflict) => Results.Problem(
        statusCode: status, title: "GA spec request blocked", extensions: new Dictionary<string, object?> { ["code"] = code });
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (GaSpecBlockedException exception) { return Problem(exception.Code); }
        catch (JsonException) { return Problem("invalid-request", StatusCodes.Status400BadRequest); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        { return Problem("adapter-or-store-unavailable", StatusCodes.Status503ServiceUnavailable); }
    }
}
