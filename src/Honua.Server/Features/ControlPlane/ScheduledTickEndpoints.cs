// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Internal, token-guarded endpoint that drives one PERIODIC control-plane tick on demand. Under
/// <c>TriggerMode=Event</c> EventBridge Scheduler (via the control-plane Lambda) POSTs here with the
/// tick kind so each bucket-(b) maintenance tick runs without an always-on in-process timer.
/// <para>
/// Mirrors the token-guard shape of the CloudDemo reset endpoint: a shared-secret header (or
/// <c>Bearer</c>) compared in fixed time, 401 when absent, 403 when present-but-wrong, 503 when the
/// server has no token configured. The route is mapped ONLY in Event mode; under Poll (default,
/// on-prem) it does not exist at all, so the in-process timers remain the sole driver.
/// </para>
/// </summary>
internal static class ScheduledTickEndpoints
{
    internal const string RoutePath = "/internal/control-plane/scheduled-tick";
    internal const string TokenHeader = "X-Honua-Control-Plane-Token";

    /// <summary>
    /// Maps the scheduled-tick endpoint when, and only when, the control plane is in Event mode.
    /// In Poll mode this is a no-op, so the surface is never exposed on on-prem deployments.
    /// </summary>
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

/// <summary>Request body for the scheduled-tick endpoint: the kind of tick to run once.</summary>
/// <param name="Kind">The <see cref="ScheduledTickKind"/> name (case-insensitive).</param>
internal sealed record ScheduledTickRequest(
    [property: JsonPropertyName("kind")] string? Kind);

/// <summary>Success response from the scheduled-tick endpoint.</summary>
/// <param name="Status">Always <c>ran</c> on success.</param>
/// <param name="Kind">The tick kind that was run.</param>
/// <param name="RanAt">When the tick completed.</param>
internal sealed record ScheduledTickResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ranAt")] DateTimeOffset RanAt);

/// <summary>Problem response from the scheduled-tick endpoint.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable message.</param>
internal sealed record ScheduledTickProblem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Source-generated JSON context for the scheduled-tick endpoint payloads (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScheduledTickRequest))]
[JsonSerializable(typeof(ScheduledTickResponse))]
[JsonSerializable(typeof(ScheduledTickProblem))]
internal sealed partial class ScheduledTickJsonContext : JsonSerializerContext;
