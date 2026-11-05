// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

// The route exists only in event mode, and a missing token refuses every request.
internal static class ScheduledTickEndpoints
{
    internal const string RoutePath = "/internal/control-plane/scheduled-tick";
    internal const string TokenHeader = "X-Honua-Control-Plane-Token";

    // The route is not mapped in poll mode, so on-prem deployments never expose it.
    public static IEndpointRouteBuilder MapScheduledTickEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!ControlPlaneTriggerModeResolver.IsEventMode(configuration))
        {
            // Poll (default, on-prem): the in-process timers drive the ticks; no endpoint is exposed.
            return endpoints;
        }

        endpoints.MapPost(RoutePath, HandleScheduledTickAsync)
            .WithDisplayName("Run Control-Plane Scheduled Tick")
            .WithTags("ControlPlane")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .AllowAnonymous()
            .Produces<ScheduledTickResponse>(StatusCodes.Status200OK, "application/json")
            .Produces<ScheduledTickProblem>(StatusCodes.Status400BadRequest, "application/json")
            .Produces<ScheduledTickProblem>(StatusCodes.Status401Unauthorized, "application/json")
            .Produces<ScheduledTickProblem>(StatusCodes.Status403Forbidden, "application/json")
            .Produces<ScheduledTickProblem>(StatusCodes.Status503ServiceUnavailable, "application/json");

        return endpoints;
    }

    private static async Task<IResult> HandleScheduledTickAsync(
        HttpContext context,
        [FromServices] IOptions<ControlPlaneTriggerOptions> options,
        [FromServices] IScheduledTickDispatcher dispatcher)
    {
        var token = options.Value.ScheduledTickToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "scheduled_tick_token_not_configured",
                "Control-plane scheduled-tick token is not configured.");
        }

        if (!TokenMatches(context.Request, token))
        {
            var statusCode = HasPresentedToken(context.Request)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return Problem(statusCode, "scheduled_tick_token_invalid", "A valid control-plane token is required.");
        }

        // A degraded start (database unreachable, or schema behind this build) answers a typed 503
        // so Scheduler retries the tick (honua-server#5916).
        if (ControlPlaneNotReadyGuard.TryRefuse(context) is { } notReady)
        {
            return notReady;
        }

        ScheduledTickRequest? request;
        try
        {
            request = await context.Request
                .ReadFromJsonAsync(ScheduledTickJsonContext.Default.ScheduledTickRequest, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "scheduled_tick_invalid_body", "Request body is not valid JSON.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Kind))
        {
            return Problem(StatusCodes.Status400BadRequest, "scheduled_tick_kind_required", "A tick 'kind' is required.");
        }

        if (!Enum.TryParse<ScheduledTickKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return Problem(StatusCodes.Status400BadRequest, "scheduled_tick_kind_unknown", $"Unknown tick kind '{request.Kind}'.");
        }

        try
        {
            await dispatcher.RunTickAsync(kind, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // No handler is registered for the kind in this deployment (the owning feature is disabled,
            // e.g. its store/Redis dependency is absent). Report 503 so the scheduler can retry/log
            // rather than treating it as a client error.
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "scheduled_tick_kind_unavailable",
                $"No scheduled-tick handler is available for kind '{kind}' in this deployment.");
        }

        return Results.Json(
            new ScheduledTickResponse("ran", kind.ToString(), DateTimeOffset.UtcNow),
            ScheduledTickJsonContext.Default.ScheduledTickResponse,
            contentType: "application/json");
    }

    private static IResult Problem(int statusCode, string code, string message)
        => Results.Json(
            new ScheduledTickProblem(code, message),
            ScheduledTickJsonContext.Default.ScheduledTickProblem,
            statusCode: statusCode,
            contentType: "application/json");

    private static bool TokenMatches(HttpRequest request, string expectedToken)
        => Honua.Server.Features.CloudDemo.CloudDemoCredentials.TokenMatches(request, TokenHeader, expectedToken);

    private static bool HasPresentedToken(HttpRequest request)
        => Honua.Server.Features.CloudDemo.CloudDemoCredentials.HasPresentedToken(request, TokenHeader);
}

internal sealed record ScheduledTickRequest(
    [property: JsonPropertyName("kind")] string? Kind);

internal sealed record ScheduledTickResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ranAt")] DateTimeOffset RanAt);

internal sealed record ScheduledTickProblem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScheduledTickRequest))]
[JsonSerializable(typeof(ScheduledTickResponse))]
[JsonSerializable(typeof(ScheduledTickProblem))]
internal sealed partial class ScheduledTickJsonContext : JsonSerializerContext;
