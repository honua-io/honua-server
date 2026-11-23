// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;

namespace Honua.Server.Features.HealthCheck;

internal sealed class QualificationFaultMiddleware(RequestDelegate next, QualificationFaultGate gate)
{
    internal const string FaultHeader = "X-Honua-Qualification-Fault";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!gate.IsConfigured || IsLoadBalancerProbe(path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var status = await gate.GetActiveAsync().ConfigureAwait(false) switch
        {
            QualificationFaultKind.Wedged => StatusCodes.Status503ServiceUnavailable,
            QualificationFaultKind.ServeErrors when !path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
                => StatusCodes.Status500InternalServerError,
            _ => 0
        };
        if (status == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers[FaultHeader] = status == StatusCodes.Status503ServiceUnavailable ? "Wedged" : "ServeErrors";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Qualification fault injected.", context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsLoadBalancerProbe(PathString path)
        => path.StartsWithSegments("/healthz/live", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/healthz/ready", StringComparison.OrdinalIgnoreCase);
}

internal static class QualificationFaultExtensions
{
    public static IServiceCollection AddQualificationFault(this IServiceCollection services, IConfiguration configuration)
    {
        var fault = QualificationFault.Parse(configuration[QualificationFault.ConfigurationKey]);
        services.AddSingleton(provider => new QualificationFaultGate(
            fault,
            provider.GetService<JobStoreDatabase>() is { } database ? new QualificationWedgeMarker(database) : null,
            provider.GetRequiredService<ILogger<QualificationFaultGate>>()));
        return services;
    }

    // The wedge is decided as the process starts, not on its first request, so every task of a
    // rollout decides inside the arming window however sparse its traffic is.
    public static IApplicationBuilder UseQualificationFault(this IApplicationBuilder app)
    {
        var gate = app.ApplicationServices.GetRequiredService<QualificationFaultGate>();
        if (gate.IsConfigured)
        {
            _ = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStarted.Register(() => _ = gate.GetActiveAsync());
        }

        return app.UseMiddleware<QualificationFaultMiddleware>();
    }
}
