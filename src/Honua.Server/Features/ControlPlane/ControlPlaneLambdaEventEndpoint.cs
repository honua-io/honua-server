// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// The in-process entrypoint for a control-plane event-handler Lambda that runs the server image
/// behind the AWS Lambda Web Adapter (the default on the aws-serverless cell, where
/// <c>control_plane_events_image</c> is unset). The adapter POSTs every non-HTTP invocation (the
/// EventBridge "Batch Job State Change" event, or an EventBridge Scheduler payload) as raw JSON to its
/// pass-through path (<c>AWS_LWA_PASS_THROUGH_PATH</c>, default <c>/events</c>) on the loopback port.
/// This route runs the handler that <c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER</c> names, once and
/// in-process:
/// <list type="bullet">
/// <item><c>batch-event</c> (alias <c>reconcile</c>): reads <c>detail.jobId</c> and reconciles that
/// execution job once.</item>
/// <item><c>backstop</c>: runs one execution-job sweep and one workflow-operation backstop sweep.</item>
/// <item><c>scheduled-tick</c>: reads <c>{"kind": "&lt;ScheduledTickKind&gt;"}</c> and runs that tick
/// once through <see cref="IScheduledTickDispatcher"/>.</item>
/// </list>
/// The route exists only on an event-handler host (the selector is set) and answers only loopback
/// callers, meaning the adapter in the same sandbox. That is the whole trust boundary, because the
/// event functions have no API Gateway or function URL. It needs no shared-secret token, unlike the
/// <c>/internal/control-plane</c> routes, which the thin <c>Honua.ControlPlane.Lambda</c> bootstrap
/// image forwards to.
/// </summary>
internal static partial class ControlPlaneLambdaEventEndpoint
{
    /// <summary>The adapter setting that moves its pass-through path.</summary>
    internal const string PassThroughPathKey = "AWS_LWA_PASS_THROUGH_PATH";

    /// <summary>The adapter's default pass-through path.</summary>
    internal const string DefaultPassThroughPath = "/events";

    internal const string BatchEventHandler = "batch-event";
    internal const string ReconcileHandlerAlias = "reconcile";
    internal const string BackstopHandler = "backstop";
    internal const string ScheduledTickHandler = "scheduled-tick";

    private const string ExpectedBatchSource = "aws.batch";
    private const string ExpectedBatchDetailType = "Batch Job State Change";

    internal sealed class ControlPlaneLambdaEventEndpointLog;

    /// <summary>The path the adapter posts events to, honouring <see cref="PassThroughPathKey"/>.</summary>
    internal static string ResolvePath(IConfiguration configuration)
    {
        var configured = configuration[PassThroughPathKey]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return DefaultPassThroughPath;
        }

        return configured.StartsWith('/') ? configured : "/" + configured;
    }

    /// <summary>
    /// Maps the adapter pass-through route on an event-handler host. Does nothing on any other host,
    /// including the API Lambda, so the route never exists where the public can reach it.
    /// </summary>
    public static IEndpointRouteBuilder MapControlPlaneLambdaEventEndpoint(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!ControlPlaneLambdaConnectionRelease.IsEventHandlerProfile(configuration))
        {
            return endpoints;
        }

        // HANDLER-AUTHORIZED: the handler admits only loopback callers (the in-sandbox web adapter) on
        // an event-handler host, which has no public ingress; see HandleAsync.
        endpoints.MapPost(ResolvePath(configuration), HandleRequestAsync)
            .WithDisplayName("Control-plane Lambda event dispatch")
            .WithTags("ControlPlane", "Internal")
            .ExcludeFromDescription()
            .AllowAnonymous();

        return endpoints;
    }

    private static Task<IResult> HandleRequestAsync(
        HttpContext context,
        [FromServices] IConfiguration configuration,
        [FromServices] IControlPlaneLambdaEventTargets targets,
        [FromServices] ILogger<ControlPlaneLambdaEventEndpointLog> logger)
        => HandleAsync(
            context,
            configuration,
            targets,
            logger,
            context.RequestServices.GetService<Honua.Infrastructure.Monitoring.DegradedStartupContext>());

    /// <summary>Dispatches one adapter invocation to the handler the selector names.</summary>
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IConfiguration configuration,
        IControlPlaneLambdaEventTargets targets,
        ILogger logger,
        Honua.Infrastructure.Monitoring.DegradedStartupContext? degradedStartup = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsLoopback(context.Connection.RemoteIpAddress))
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "control_plane_lambda_event_not_loopback",
                "Control-plane Lambda events are accepted only from the local web adapter.");
        }

        // A degraded start (database unreachable, or the schema behind this build before the
        // migration task ran) cannot do the control-plane work. Answer a typed not-ready 503 so the
        // invocation fails and EventBridge/Scheduler retries it, instead of running the handler
        // against a database it cannot use (honua-server#5916).
        if (ControlPlaneNotReadyGuard.TryRefuse(context, degradedStartup) is { } notReady)
        {
            LogNotReady(logger, context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString());
            return notReady;
        }

        var selector = configuration[ControlPlaneLambdaConnectionRelease.HandlerSelectorKey]?.Trim() ?? string.Empty;
        var cancellationToken = context.RequestAborted;

        if (IsHandler(selector, BatchEventHandler) || IsHandler(selector, ReconcileHandlerAlias))
        {
            using var document = await ReadJsonAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var providerOperationId = ExtractBatchJobId(document);
            if (providerOperationId is null)
            {
                LogBatchEventIgnored(logger);
                return Ok(BatchEventHandler, "ignored", null);
            }

            LogReconcilingBatchJob(logger, providerOperationId);
            await targets.ReconcileBatchJobAsync(providerOperationId, cancellationToken).ConfigureAwait(false);
            return Ok(BatchEventHandler, "ran", providerOperationId);
        }

        if (IsHandler(selector, BackstopHandler))
        {
            // The Scheduler payload is opaque; the handler sweeps once whatever it carries.
            LogRunningBackstop(logger);
            await targets.RunBackstopAsync(cancellationToken).ConfigureAwait(false);
            return Ok(BackstopHandler, "ran", null);
        }

        if (IsHandler(selector, ScheduledTickHandler))
        {
            using var document = await ReadJsonAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var kindName = ReadString(document?.RootElement, "kind");
            if (string.IsNullOrWhiteSpace(kindName))
            {
                return Problem(StatusCodes.Status400BadRequest, "scheduled_tick_kind_required", "A tick 'kind' is required.");
            }

            if (!Enum.TryParse<ScheduledTickKind>(kindName, ignoreCase: true, out var kind)
                || !Enum.IsDefined(kind))
            {
                return Problem(StatusCodes.Status400BadRequest, "scheduled_tick_kind_unknown", $"Unknown tick kind '{kindName}'.");
            }

            LogRunningScheduledTick(logger, kind.ToString());
            try
            {
                await targets.RunScheduledTickAsync(kind, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Same contract as the scheduled-tick endpoint: the owning feature is disabled here.
                return Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    "scheduled_tick_kind_unavailable",
                    $"No scheduled-tick handler is available for kind '{kind}' in this deployment.");
            }

            return Ok(ScheduledTickHandler, "ran", kind.ToString());
        }

        LogUnknownHandler(logger, selector);
        return Problem(
            StatusCodes.Status400BadRequest,
            "control_plane_lambda_handler_unknown",
            $"{ControlPlaneLambdaConnectionRelease.HandlerSelectorKey} '{selector}' is not one of "
            + $"{BatchEventHandler}, {ReconcileHandlerAlias}, {BackstopHandler}, {ScheduledTickHandler}.");
    }

    /// <summary>
    /// Reads the AWS Batch job id from a "Batch Job State Change" event. Returns <see langword="null"/>
    /// for a missing, malformed, or mis-routed event, which the caller treats as nothing to reconcile.
    /// </summary>
    internal static string? ExtractBatchJobId(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var source = ReadString(root, "source");
        if (!string.IsNullOrEmpty(source) && !string.Equals(source, ExpectedBatchSource, StringComparison.Ordinal))
        {
            return null;
        }

        var detailType = ReadString(root, "detail-type");
        if (!string.IsNullOrEmpty(detailType)
            && !string.Equals(detailType, ExpectedBatchDetailType, StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("detail", out var detail))
        {
            return null;
        }

        var jobId = ReadString(detail, "jobId");
        return string.IsNullOrWhiteSpace(jobId) ? null : jobId;
    }

    private static bool IsHandler(string selector, string handler)
        => string.Equals(selector, handler, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(IPAddress? address)
        => address is not null
           && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static async Task<JsonDocument?> ReadJsonAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(request.Body, default, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(property, out var found)
           && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static IResult Ok(string handler, string status, string? subject)
        => Results.Json(
            new ControlPlaneLambdaEventResult(handler, status, subject),
            ControlPlaneLambdaEventJsonContext.Default.ControlPlaneLambdaEventResult,
            contentType: "application/json");

    private static IResult Problem(int statusCode, string code, string message)
        => Results.Json(
            new ControlPlaneEventProblem(code, message),
            ControlPlaneLambdaEventJsonContext.Default.ControlPlaneEventProblem,
            statusCode: statusCode,
            contentType: "application/problem+json");

    [LoggerMessage(19630, LogLevel.Information, "Control-plane Lambda batch event carried no actionable jobId; nothing to reconcile")]
    private static partial void LogBatchEventIgnored(ILogger logger);

    [LoggerMessage(19631, LogLevel.Information, "Control-plane Lambda reconciling execution job for Batch jobId {ProviderOperationId}")]
    private static partial void LogReconcilingBatchJob(ILogger logger, string providerOperationId);

    [LoggerMessage(19632, LogLevel.Information, "Control-plane Lambda running one backstop sweep")]
    private static partial void LogRunningBackstop(ILogger logger);

    [LoggerMessage(19633, LogLevel.Information, "Control-plane Lambda running scheduled tick {Kind}")]
    private static partial void LogRunningScheduledTick(ILogger logger, string kind);

    [LoggerMessage(19635, LogLevel.Warning, "Control-plane Lambda event refused: the host is not ready ({ReasonCode}); returning 503 so the event is retried")]
    private static partial void LogNotReady(ILogger logger, string reasonCode);

    [LoggerMessage(19634, LogLevel.Error, "Control-plane Lambda handler selector '{Selector}' is not recognised; the event was not handled")]
    private static partial void LogUnknownHandler(ILogger logger, string selector);
}

/// <summary>The work a control-plane event-handler Lambda invocation can run, once each.</summary>
internal interface IControlPlaneLambdaEventTargets
{
    /// <summary>Reconciles the execution job that owns an AWS Batch job id.</summary>
    Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken);

    /// <summary>Runs one execution-job and one workflow-operation backstop sweep.</summary>
    Task RunBackstopAsync(CancellationToken cancellationToken);

    /// <summary>Runs one periodic tick.</summary>
    Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken);
}

/// <summary>
/// Binds <see cref="IControlPlaneLambdaEventTargets"/> to the services the internal control-plane
/// routes already use, so both Lambda images run the same code.
/// </summary>
internal sealed class ControlPlaneLambdaEventTargets(
    IServiceProvider services,
    IOptions<ControlPlaneTriggerOptions> options) : IControlPlaneLambdaEventTargets
{
    public Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken)
        => services.GetRequiredService<ControlPlaneEventHandler>()
            .HandleExecutionJobEventAsync(providerOperationId, cancellationToken);

    public async Task RunBackstopAsync(CancellationToken cancellationToken)
    {
        var staleThreshold = options.Value.StaleThreshold > TimeSpan.Zero
            ? options.Value.StaleThreshold
            : TimeSpan.FromSeconds(90);

        // An event-handler host does not host either backstop timer (Program.cs), so this invocation
        // is the only thing that sweeps execution jobs and deploy/metadata/coordinated operations.
        await services.GetRequiredService<ExecutionJobBackstopSweepService>()
            .SweepOnceAsync(staleThreshold, cancellationToken)
            .ConfigureAwait(false);
        await services.GetRequiredService<WorkflowOperationBackstopSweepService>()
            .SweepOnceAsync(staleThreshold, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken)
        => services.GetRequiredService<IScheduledTickDispatcher>().RunTickAsync(kind, cancellationToken);
}

/// <summary>Result of one control-plane Lambda event invocation.</summary>
/// <param name="Handler">The handler that ran.</param>
/// <param name="Status"><c>ran</c>, or <c>ignored</c> when the event carried nothing to do.</param>
/// <param name="Subject">The Batch job id or tick kind, when there is one.</param>
internal sealed record ControlPlaneLambdaEventResult(
    [property: JsonPropertyName("handler")] string Handler,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("subject")] string? Subject);

/// <summary>Source-generated JSON context for the control-plane Lambda event route (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ControlPlaneLambdaEventResult))]
[JsonSerializable(typeof(ControlPlaneEventProblem))]
internal sealed partial class ControlPlaneLambdaEventJsonContext : JsonSerializerContext;
