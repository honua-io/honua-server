// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Internal HTTP entrypoints that drive a single execution-job reconcile / backstop sweep from an
/// out-of-band trigger instead of the in-process poll loop. These are the cloud (event-driven)
/// surface: an AWS Lambda invoked by EventBridge ("Batch Job State Change") or EventBridge Scheduler
/// (the backstop timer) posts to these routes, the full Honua.Server host (running under the AWS
/// Lambda Web Adapter) resolves the already-wired <see cref="ControlPlaneEventHandler"/> /
/// <see cref="ExecutionJobBackstopSweepService"/>, reconciles once, and returns.
/// <para>
/// Hosting the reconcile on the FULL server graph (rather than re-composing the deep reconciler +
/// store + backend dependency tree in a standalone Lambda) is deliberate: the dispatcher's typed
/// reconcilers transitively need most of the server's infrastructure (durable stores, batch/deploy
/// backends, metadata + data-plane services), so the one faithful composition is the server's own.
/// The thin <c>Honua.ControlPlane.Lambda</c> entrypoint only parses the EventBridge event into a
/// provider job id and posts here; this keeps a single source of truth for the reconcile graph and
/// is AOT-trivial (query-string binding, no request-body JSON contract to source-generate).
/// </para>
/// <para>
/// The routes are mapped only when <c>ControlPlane:TriggerMode = Event</c> and are protected by a
/// shared-secret header (<c>X-Honua-ControlPlane-Token</c>) when a token is configured, because they
/// are an internal control surface reachable only from the EventBridge-invoked Lambda inside the
/// deployment's trust boundary, not a public API.
/// </para>
/// </summary>
internal static class ControlPlaneEventEndpoints
{
    /// <summary>Header carrying the shared secret that authorizes an event/backstop invocation.</summary>
    internal const string TokenHeader = "X-Honua-ControlPlane-Token";

    internal sealed class ControlPlaneEventEndpointsLog;

    /// <summary>
    /// Maps the internal control-plane event + backstop routes. No-op unless the configured
    /// <see cref="ControlPlaneTriggerOptions.TriggerMode"/> is <see cref="ControlPlaneTriggerMode.Event"/>,
    /// so on-prem (poll) deployments never expose this surface.
    /// </summary>
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
        // (see IsAuthorized) and are reachable only from the EventBridge-invoked Lambda inside
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

    /// <summary>
    /// Reconciles the execution-job operation referenced by an AWS Batch state-change event once.
    /// The Lambda has already extracted <c>detail.jobId</c> (the AWS Batch provider id) and passes it
    /// as the <c>providerOperationId</c> query parameter.
    /// </summary>
    private static async Task<IResult> HandleBatchJobStateChangeAsync(
        [FromQuery] string? providerOperationId,
        [FromServices] ControlPlaneEventHandler handler,
        [FromServices] IConfiguration configuration,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(httpRequest, configuration))
        {
            return Results.Unauthorized();
        }

        await handler
            .HandleExecutionJobEventAsync(providerOperationId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok();
    }

    /// <summary>
    /// Runs one backstop sweep (reconcile every stale non-terminal execution job) and returns.
    /// EventBridge Scheduler posts here on a coarse timer so dropped/missed events self-heal.
    /// </summary>
    private static async Task<IResult> HandleBackstopSweepAsync(
        [FromServices] ExecutionJobBackstopSweepService backstop,
        [FromServices] IOptions<ControlPlaneTriggerOptions> options,
        [FromServices] IConfiguration configuration,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(httpRequest, configuration))
        {
            return Results.Unauthorized();
        }

        var staleThreshold = options.Value.StaleThreshold > TimeSpan.Zero
            ? options.Value.StaleThreshold
            : TimeSpan.FromSeconds(90);

        await backstop.SweepOnceAsync(staleThreshold, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    private static bool IsAuthorized(HttpRequest httpRequest, IConfiguration configuration)
    {
        var expected = configuration["ControlPlane:EventToken"]
            ?? configuration["HONUA_CONTROL_PLANE_EVENT_TOKEN"];

        // No token configured => the surface is reachable only inside the deployment trust boundary
        // (private VPC, EventBridge-invoked Lambda). When a token IS configured it is required and
        // compared in fixed time.
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        if (!httpRequest.Headers.TryGetValue(TokenHeader, out var provided) || provided.Count != 1)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided.ToString()),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
