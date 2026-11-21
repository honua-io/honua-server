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
            || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            await WriteTextAsync(context, StatusCodes.Status400BadRequest, "Only loopback reference-server URLs without credentials are allowed.").ConfigureAwait(false);
            return;
        }

        try
        {
            await ForwardAsync(context, uri).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteTextAsync(context, StatusCodes.Status504GatewayTimeout, "Reference server request timed out.").ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteTextAsync(context, StatusCodes.Status502BadGateway, "Reference server request failed.").ConfigureAwait(false);
        }
    }

    private static async Task ForwardAsync(HttpContext context, Uri uri)
    {
        using var httpClient = new HttpClient { Timeout = ProbeTimeout };
        using var response = await httpClient.GetAsync(uri, context.RequestAborted).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteTextAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
