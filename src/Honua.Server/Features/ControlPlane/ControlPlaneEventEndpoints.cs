// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.CloudDemo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

// These routes exist only in event mode and are an internal control surface guarded by the shared-secret header.
internal static class ControlPlaneEventEndpoints
{
    internal const string TokenHeader = "X-Honua-ControlPlane-Token";

    internal sealed class ControlPlaneEventEndpointsLog;

    // The routes are not mapped unless trigger mode is Event, so poll deployments never expose them.
    public static void MapControlPlaneEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ControlPlaneTriggerOptions>>().Value;
        if (options.TriggerMode != ControlPlaneTriggerMode.Event)
        {
            return;
        }

        // HANDLER-AUTHORIZED (#1144): these internal event/backstop routes enforce their own
        // authorization in-handler via the shared-secret X-Honua-ControlPlane-Token check
        // (see CheckAuthorization) and are reachable only from the EventBridge-invoked Lambda inside
        // the deployment trust boundary — they are not a framework-policy surface. Marked
        // AllowAnonymous on the group so the audit architecture guard records the explicit,
        // intentional decision for both child mutation routes.
        var group = endpoints.MapGroup("/internal/control-plane")
            .WithTags("ControlPlane", "Internal")
            .ExcludeFromDescription()
            .AllowAnonymous();

        group.MapPost("/events/batch-job-state-change", HandleBatchJobStateChangeAsync)
            .WithDisplayName("Control-plane Batch job state-change reconcile");

        group.MapPost("/backstop/sweep", HandleBackstopSweepAsync)
            .WithDisplayName("Control-plane backstop sweep");
    }

    private static async Task<IResult> HandleBatchJobStateChangeAsync(
        [FromQuery] string? providerOperationId,
        [FromServices] ControlPlaneEventHandler handler,
        [FromServices] IConfiguration configuration,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        var authResult = CheckAuthorization(httpRequest, configuration);
        if (authResult is not null)
        {
            return authResult;
        }

        // A degraded start (database unreachable, or schema behind this build) answers a typed 503
        // so the event is retried (honua-server#5916).
        if (ControlPlaneNotReadyGuard.TryRefuse(httpRequest.HttpContext) is { } notReady)
        {
            return notReady;
        }

        await handler
            .HandleExecutionJobEventAsync(providerOperationId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok();
    }

    private static async Task<IResult> HandleBackstopSweepAsync(
        [FromServices] ExecutionJobBackstopSweepService backstop,
        [FromServices] IOptions<ControlPlaneTriggerOptions> options,
        [FromServices] IConfiguration configuration,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        var authResult = CheckAuthorization(httpRequest, configuration);
        if (authResult is not null)
        {
            return authResult;
        }

        // A degraded start (database unreachable, or schema behind this build) answers a typed 503
        // so the event is retried (honua-server#5916).
        if (ControlPlaneNotReadyGuard.TryRefuse(httpRequest.HttpContext) is { } notReady)
        {
            return notReady;
        }

        var staleThreshold = options.Value.StaleThreshold > TimeSpan.Zero
            ? options.Value.StaleThreshold
            : TimeSpan.FromSeconds(90);

        await backstop.SweepOnceAsync(staleThreshold, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    // Authorization fails closed with 503 when the token is not configured, so an unconfigured deployment cannot be invoked unauthenticated.
    internal static IResult? CheckAuthorization(HttpRequest httpRequest, IConfiguration configuration)
    {
        var expected = configuration["ControlPlane:EventToken"]
            ?? configuration["HONUA_CONTROL_PLANE_EVENT_TOKEN"];

        // No token configured => fail closed (PA-060). Previously this returned true (fail-open),
        // allowing unauthenticated access on deployments that had not set a token. Now 503 is
        // returned so operators discover a misconfiguration rather than silently getting no auth.
        if (string.IsNullOrEmpty(expected))
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "control_plane_event_token_not_configured",
                "Control-plane event token is not configured.");
        }

        if (!CloudDemoCredentials.TokenMatches(httpRequest, TokenHeader, expected))
        {
            var statusCode = CloudDemoCredentials.HasPresentedToken(httpRequest, TokenHeader)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return Problem(statusCode, "control_plane_event_token_invalid", "A valid control-plane token is required.");
        }

        return null; // authorized
    }

    private static IResult Problem(int statusCode, string code, string message)
        => Results.Json(
            new ControlPlaneEventProblem(code, message),
            ControlPlaneEventJsonContext.Default.ControlPlaneEventProblem,
            statusCode: statusCode,
            contentType: "application/json");
}

internal sealed record ControlPlaneEventProblem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ControlPlaneEventProblem))]
internal sealed partial class ControlPlaneEventJsonContext : JsonSerializerContext;
