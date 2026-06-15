// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Plugins;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Plugins;

/// <summary>
/// Maps plugin-contributed REST routes (<see cref="ICustomEndpoint"/>, issue #1562). Each registered
/// custom endpoint is mapped once at startup under the reserved <c>/plugins</c> prefix using the
/// AOT-safe <c>MapMethods</c> + explicit HTTP-metadata pattern (no runtime route discovery). Routes
/// are gated, at request time, behind the Enterprise <c>plugin.sdk</c> entitlement and the operator
/// kill-switch, and pass through the host's authorization metadata when the plugin requests it — a
/// plugin cannot bypass the shared authorization pipeline.
/// </summary>
public static class PluginCustomEndpoints
{
    /// <summary>Reserved route prefix all plugin-contributed endpoints are mounted under.</summary>
    public const string RoutePrefix = "/plugins";

    /// <summary>
    /// Maps every registered <see cref="ICustomEndpoint"/> under the reserved <c>/plugins</c> prefix.
    /// A no-op when no plugins contribute endpoints. Safe to call unconditionally in the host
    /// composition root: the per-request license/kill-switch gate keeps contributed routes inert
    /// unless the SDK is licensed and enabled.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IEndpointRouteBuilder MapHonuaPluginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var catalog = endpoints.ServiceProvider.GetService<PluginCatalog>();
        if (catalog is null || !catalog.Plugins.Any(p => p.ProvidesCustomEndpoint))
        {
            return endpoints;
        }

        var customEndpoints = endpoints.ServiceProvider.GetServices<ICustomEndpoint>().ToArray();
        foreach (var endpoint in customEndpoints)
        {
            MapOne(endpoints, endpoint);
        }

        return endpoints;
    }

    private static void MapOne(IEndpointRouteBuilder endpoints, ICustomEndpoint endpoint)
    {
        var methods = endpoint.Methods is { Count: > 0 }
            ? endpoint.Methods.ToArray()
            : throw new InvalidOperationException(
                $"Plugin custom endpoint '{endpoint.GetType().FullName}' must declare at least one HTTP method.");

        if (string.IsNullOrWhiteSpace(endpoint.Pattern))
        {
            throw new InvalidOperationException(
                $"Plugin custom endpoint '{endpoint.GetType().FullName}' must declare a non-empty route pattern.");
        }

        var pattern = $"{RoutePrefix}/{endpoint.Pattern.TrimStart('/')}";

        // Capture the singleton endpoint instance so the handler delegate is static-rooted and
        // AOT-safe (no per-request service resolution of the plugin type, no reflection).
        var builder = endpoints.MapMethods(pattern, methods, (HttpContext context, CancellationToken ct)
            => HandleAsync(endpoint, context, ct))
            .WithTags("Plugins")
            .WithDisplayName($"Plugin endpoint: {pattern}");

        if (endpoint.RequiresAuthorization)
        {
            builder.RequireAuthorization();
        }
    }

    private static async Task<IResult> HandleAsync(
        ICustomEndpoint endpoint,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // License + kill-switch gate, enforced per request so a license change takes effect without
        // a restart. Contributed routes are Enterprise-gated like the rest of the plugin SDK.
        var options = context.RequestServices.GetService<IOptions<PluginOptions>>();
        if (options is { Value.Enabled: false })
        {
            return Results.NotFound();
        }

        if (!LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.PluginSdkKey))
        {
            return Results.NotFound();
        }

        var request = await BuildRequestAsync(context, cancellationToken).ConfigureAwait(false);
        var response = await endpoint.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return new PluginEndpointResult(response);
    }

    private static async Task<PluginEndpointRequest> BuildRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var routeValues = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.RouteValues)
        {
            routeValues[key] = value?.ToString();
        }

        var query = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.Query)
        {
            query[key] = value.ToString();
        }

        var headers = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.Headers)
        {
            headers[key] = value.ToString();
        }

        var body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        return new PluginEndpointRequest(
            context.Request.Method.ToUpperInvariant(),
            context.Request.Path.Value ?? string.Empty,
            routeValues.ToImmutable(),
            query.ToImmutable(),
            headers.ToImmutable(),
            body,
            context.User?.Identity?.Name);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is null or 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes a plugin-produced <see cref="PluginEndpointResponse"/> back to the caller. Implemented
    /// as an <see cref="IResult"/> so the contributed route participates in the standard response
    /// pipeline (logging, telemetry, exception handling) like every other endpoint.
    /// </summary>
    private sealed class PluginEndpointResult(PluginEndpointResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = response.StatusCode;
            if (!string.IsNullOrEmpty(response.ContentType))
            {
                httpContext.Response.ContentType = response.ContentType;
            }

            foreach (var (key, value) in response.Headers)
            {
                httpContext.Response.Headers[key] = value;
            }

            if (!response.Body.IsEmpty)
            {
                await httpContext.Response.Body
                    .WriteAsync(response.Body, httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }
}
