// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.CrossServerConsume;

// Test-host-only passthrough to a loopback reference server, so the consume tests read the
// reference server through the Honua test host. It lives here, not in the product binary.
internal sealed class CrossServerConsumeProbe : IStartupFilter
{
    internal const string Path = "/__test/cross-server-consume/proxy";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    public static void Register(IServiceCollection services)
        => services.AddTransient<IStartupFilter, CrossServerConsumeProbe>();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Map(Path, branch => branch.Run(HandleAsync));
            next(app);
        };

    private static async Task HandleAsync(HttpContext context)
    {
        var sourceUrl = context.Request.Query["url"].ToString();
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Only loopback reference-server URLs are allowed.").ConfigureAwait(false);
            return;
        }

        using var httpClient = new HttpClient { Timeout = ProbeTimeout };
        using var response = await httpClient.GetAsync(uri, context.RequestAborted).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
