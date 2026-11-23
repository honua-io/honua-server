// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using Honua.Aws.Features.ControlPlane;
using Honua.ControlPlane;
using Npgsql;
#endif

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

        var status = await gate.GetActiveAsync(context.RequestAborted).ConfigureAwait(false) switch
        {
            QualificationFaultKind.Wedged => StatusCodes.Status503ServiceUnavailable,
            QualificationFaultKind.ServeErrors when !IsProbe(path, "/healthz/deep")
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
        => IsProbe(path, "/healthz/live") || IsProbe(path, "/healthz/ready");

    private static bool IsProbe(PathString path, string probe)
        => path.Equals(probe, StringComparison.OrdinalIgnoreCase)
            || path.Equals(probe + "/", StringComparison.OrdinalIgnoreCase);
}

internal static class QualificationFaultExtensions
{
    public static IServiceCollection AddQualificationFault(this IServiceCollection services, IConfiguration configuration)
    {
        var fault = QualificationFault.Parse(configuration[QualificationFault.ConfigurationKey]);
        // Only the wedge needs the shared database, so installs without it never resolve the store.
        services.AddSingleton(provider => new QualificationFaultGate(
            fault,
            fault.Kind == QualificationFaultKind.Wedged
                ? ResolveMarker(provider)
                : null,
            provider.GetRequiredService<ILogger<QualificationFaultGate>>(),
            provider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None));
        return services;
    }

    private static IQualificationWedgeMarker? ResolveMarker(IServiceProvider provider)
    {
        var marker = provider.GetService<IQualificationWedgeMarker>();
#if HONUA_EXCLUDE_AWS
        return marker;
#else
        if (marker is not null || provider.GetService<NpgsqlDataSource>() is null)
        {
            return marker;
        }

        return provider.GetService<JobStoreDatabase>() is { } database
            ? new QualificationWedgeMarker(database, QualificationEcsDeployment.ReadAsync)
            : null;
#endif
    }

    // Decide at process start so the probe does not have to initiate discovery.
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
