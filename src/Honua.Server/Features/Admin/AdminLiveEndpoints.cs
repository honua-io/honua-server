// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;

namespace Honua.Server.Features.Admin;

internal static class AdminLiveEndpoints
{
    public static IServiceCollection AddAdminRealtime(this IServiceCollection services)
    {
        services.AddSingleton<AdminLiveStream>();
        services.AddSingleton<AdminRealtimePublisher>();
        services.AddSingleton<AdminRealtimeBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<AdminRealtimeBroadcaster>());
        services.AddSingleton<Honua.ControlPlane.IControlPlaneRealtimeWriter, AdminControlPlaneRealtimeWriter>();
        return services;
    }

    public static IEndpointConventionBuilder MapAdminLive(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(AdminRealtimeContract.LivePath, HandleAsync)
            .WithDisplayName("Admin live view")
            .WithName("GetAdminLive")
            .WithSummary("Stream admin live events")
            .WithDescription("SSE over the admin family. The optional comma-separated topics query selects proposals, ops-health, operate-events or deploy-operations; omission selects all. Last-Event-ID accepts an admin:<sequence> cursor for replay within 24 hours. A status frame precedes events; reset requires a REST refresh.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
            .RequireAdminAuthorization();

    private static Task HandleAsync(HttpContext context, AdminLiveStream stream) => stream.HandleAsync(context);
}
