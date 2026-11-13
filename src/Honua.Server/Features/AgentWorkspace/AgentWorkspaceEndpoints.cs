// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentWorkspaceEndpoints
{
    public static IServiceCollection AddLocalProcessWorkspaces(this IServiceCollection services)
    {
        services.AddSingleton<AgentWorkspaceIdentityStore>();
        services.AddScoped<AgentWorkspaceAuthorization>();
        services.AddHttpClient<ILocalProcessWorkspaceGateway, LocalProcessWorkspaceGateway>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        return services;
    }

    public static void MapLocalProcessWorkspaces(this IEndpointRouteBuilder endpoints)
    {
        var versions = endpoints.NewApiVersionSet().HasApiVersion(new Asp.Versioning.ApiVersion(1, 0)).Build();
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/agent")
            .WithApiVersionSet(versions).HasApiVersion(1, 0).WithTags("Studio")
            .RequireStudioLifecycleAuthorization();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(invocation).ConfigureAwait(false);
            }
            catch (LocalProcessWorkspaceException exception)
            {
                return Results.Json(new LocalProcessError(exception.Message), LocalProcessJsonContext.Default.LocalProcessError, statusCode: exception.StatusCode);
            }
        });
        group.MapPost("/workspaces", Create);
        group.MapGet("/workspaces/{workspaceId}", Get);
        group.MapLocalProcesses();
    }

    private static async Task<IResult> Create(HttpContext context, AgentWorkspaceAuthorization authorization, AgentWorkspaceIdentityStore store)
    {
        var access = authorization.ResolveCaller(context);
        var identity = new AgentWorkspaceIdentity(Guid.NewGuid().ToString("N"), access.ActorId, access.TargetId, access.TenantId);
        await store.CreateAsync(identity, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(identity, LocalProcessJsonContext.Default.AgentWorkspaceIdentity, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Get(HttpContext context, string workspaceId, AgentWorkspaceAuthorization authorization)
    {
        var access = await authorization.AuthorizeAsync(context, workspaceId).ConfigureAwait(false);
        var identity = new AgentWorkspaceIdentity(access.WorkspaceId, access.OwnerActorId, access.TargetId, access.TenantId);
        return Results.Json(identity, LocalProcessJsonContext.Default.AgentWorkspaceIdentity);
    }
}
