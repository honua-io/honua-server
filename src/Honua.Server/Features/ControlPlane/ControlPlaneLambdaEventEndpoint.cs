// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Models;
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
/// On a Redis-off cell the durable job and workflow-operation stores are not composed, so
/// <c>batch-event</c>, <c>backstop</c>, and the Redis-backed tick kinds answer a typed
/// capability-unavailable <c>503</c> (<c>missingDependency=redis</c>) while the other tick kinds
/// still run. Any other failure answers a problem+json <c>500</c> that names the exception type.
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
        [FromServices] ILogger<ControlPlaneLambdaEventEndpointLog> logger)
        // Resolved here rather than bound with [FromServices]: a host that never composed the
        // targets must answer a typed refusal, not an unhandled binding exception (honua-release
        // run 38242272260, Redis-off cell).
        => HandleAsync(
            context,
            configuration,
            context.RequestServices.GetService<IControlPlaneLambdaEventTargets>(),
            logger,
            degradedStartup: context.RequestServices.GetService<Honua.Infrastructure.Monitoring.DegradedStartupContext>());

    /// <summary>Dispatches one adapter invocation to the handler the selector names.</summary>
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IConfiguration configuration,
        IControlPlaneLambdaEventTargets? targets,
        ILogger logger,
        ColdStartWarningLatch? warningLatch = null,
        Honua.Infrastructure.Monitoring.DegradedStartupContext? degradedStartup = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);
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
        var handler = CanonicalHandler(selector);
        if (handler is null)
        {
            LogUnknownHandler(logger, selector);
            return Problem(
                StatusCodes.Status400BadRequest,
                "control_plane_lambda_handler_unknown",
                $"{ControlPlaneLambdaConnectionRelease.HandlerSelectorKey} '{selector}' is not one of "
                + $"{BatchEventHandler}, {ReconcileHandlerAlias}, {BackstopHandler}, {ScheduledTickHandler}.");
        }

        if (targets is null)
        {
            LogTargetsNotComposed(logger, handler);
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                "control_plane_not_ready",
                $"The '{handler}' control-plane event handler is not composed on this host.");
        }

        var cancellationToken = context.RequestAborted;
        try
        {
            return handler switch
            {
                BatchEventHandler => await RunBatchEventAsync(context, targets, logger, cancellationToken).ConfigureAwait(false),
                BackstopHandler => await RunBackstopAsync(targets, logger, cancellationToken).ConfigureAwait(false),
                _ => await RunScheduledTickAsync(context, targets, logger, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (ControlPlaneEventDependencyUnavailableException unavailable)
        {
            var latchKey = handler + ":" + (unavailable.Subject ?? string.Empty);
            if ((warningLatch ?? ColdStartWarningLatch.Shared).TryEnter(latchKey))
            {
                LogDependencyUnavailable(logger, handler, unavailable.Subject ?? "-", unavailable.MissingDependency);
            }

            return DependencyUnavailable(context, handler, unavailable.Subject);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The adapter needs a diagnosable answer for any handler failure.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            LogHandlerFailed(logger, exception, handler, exceptionType);
            return Problem(
                StatusCodes.Status500InternalServerError,
                "control_plane_lambda_handler_failed",
                $"The '{handler}' control-plane event handler failed with {exceptionType}. "
                + "The function log has the stack trace.");
        }
    }

    private static async Task<IResult> RunBatchEventAsync(
        HttpContext context,
        IControlPlaneLambdaEventTargets targets,
        ILogger logger,
        CancellationToken cancellationToken)
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

    private static async Task<IResult> RunBackstopAsync(
        IControlPlaneLambdaEventTargets targets,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The Scheduler payload is opaque; the handler sweeps once whatever it carries.
        LogRunningBackstop(logger);
        await targets.RunBackstopAsync(cancellationToken).ConfigureAwait(false);
        return Ok(BackstopHandler, "ran", null);
    }

    private static async Task<IResult> RunScheduledTickAsync(
        HttpContext context,
        IControlPlaneLambdaEventTargets targets,
        ILogger logger,
        CancellationToken cancellationToken)
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

    /// <summary>
    /// The canonical handler name for a selector (<c>reconcile</c> maps to <c>batch-event</c>), or
    /// <see langword="null"/> when the selector names no handler.
    /// </summary>
    internal static string? CanonicalHandler(string selector)
    {
        if (IsHandler(selector, BatchEventHandler) || IsHandler(selector, ReconcileHandlerAlias))
        {
            return BatchEventHandler;
        }

        if (IsHandler(selector, BackstopHandler))
        {
            return BackstopHandler;
        }

        return IsHandler(selector, ScheduledTickHandler) ? ScheduledTickHandler : null;
    }

    /// <summary>
    /// The capability-unavailable refusal for a handler whose durable store was never composed. It
    /// reports the composition cause the same way the proposal and geoprocessing refusals do: a
    /// present but unentitled Redis is a licence problem, anything else is a missing Redis.
    /// </summary>
    private static IResult DependencyUnavailable(HttpContext context, string handler, string? subject)
    {
        var substrate = context.RequestServices?.GetService<IOptions<DurableJobSubstrateOptions>>()?.Value
            ?? new DurableJobSubstrateOptions();
        var unentitled = substrate.Classify(jobStorePresent: false, jobQueuePresent: false)
            == DurableJobSubstrateCause.RedisNotEntitled;
        var work = subject is null
            ? $"The '{handler}' control-plane event handler"
            : $"The '{handler}' handler for '{subject}'";

        return unentitled
            ? ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                work + " needs the durable job and workflow-operation stores, which require the Pro "
                + "'caching.redis' entitlement. Redis is configured on this server, but the active licence "
                + "does not entitle it, so the invocation was not run.",
                missingDependency: null,
                CapabilityUnavailableCodes.EntitlementRemediation,
                CapabilityUnavailableCodes.EntitlementRemediationRef,
                CapabilityUnavailableCodes.DurableJobsCapability,
                errorCode: CapabilityUnavailableCodes.EntitlementErrorCode,
                missingEntitlement: CapabilityUnavailableCodes.RedisCacheEntitlement)
            : ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                work + " needs the Redis-backed durable job and workflow-operation stores. This server "
                + "was started without Redis, so the invocation was not run; serving, configuration, and "
                + "style/render are unaffected.",
                CapabilityUnavailableCodes.RedisDependency,
                CapabilityUnavailableCodes.RedisRemediation,
                CapabilityUnavailableCodes.RedisRemediationRef,
                CapabilityUnavailableCodes.DurableJobsCapability);
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

    [LoggerMessage(19638, LogLevel.Warning, "Control-plane Lambda event refused: the host is not ready ({ReasonCode}); returning 503 so the event is retried")]
    private static partial void LogNotReady(ILogger logger, string reasonCode);

    [LoggerMessage(19634, LogLevel.Error, "Control-plane Lambda handler selector '{Selector}' is not recognised; the event was not handled")]
    private static partial void LogUnknownHandler(ILogger logger, string selector);

    [LoggerMessage(19635, LogLevel.Warning, "Control-plane Lambda handler '{Handler}' ({Subject}) refused: missing dependency {MissingDependency}. This host has no durable control-plane store (Redis-off cell); further refusals from this instance are not logged")]
    private static partial void LogDependencyUnavailable(ILogger logger, string handler, string subject, string missingDependency);

    [LoggerMessage(19636, LogLevel.Error, "Control-plane Lambda handler '{Handler}' failed with {ExceptionType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handler, string exceptionType);

    [LoggerMessage(19637, LogLevel.Error, "Control-plane Lambda handler '{Handler}' cannot run: IControlPlaneLambdaEventTargets is not registered on this host")]
    private static partial void LogTargetsNotComposed(ILogger logger, string handler);
}

/// <summary>
/// Remembers which refusals this instance has already logged, so a Redis-off event function logs its
/// missing dependency once per cold start rather than on every invocation.
/// </summary>
internal sealed class ColdStartWarningLatch
{
    private readonly ConcurrentDictionary<string, byte> _entered = new(StringComparer.Ordinal);

    /// <summary>The process-wide latch the route uses.</summary>
    public static ColdStartWarningLatch Shared { get; } = new();

    /// <summary>Returns <see langword="true"/> the first time <paramref name="key"/> is seen.</summary>
    public bool TryEnter(string key) => _entered.TryAdd(key, 0);
}

/// <summary>
/// Raised by <see cref="IControlPlaneLambdaEventTargets"/> when the work an invocation names needs a
/// store this host never composed (the Redis-backed durable control plane on a Redis-off cell).
/// </summary>
internal sealed class ControlPlaneEventDependencyUnavailableException : Exception
{
    public ControlPlaneEventDependencyUnavailableException(string missingDependency, string? subject)
        : base(subject is null
            ? $"The control-plane event work needs '{missingDependency}', which this host did not compose."
            : $"The control-plane event work '{subject}' needs '{missingDependency}', which this host did not compose.")
    {
        MissingDependency = missingDependency;
        Subject = subject;
    }

    /// <summary>The dependency that was not composed (<c>redis</c>).</summary>
    public string MissingDependency { get; }

    /// <summary>The tick kind, when the refusal is for one tick.</summary>
    public string? Subject { get; }
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
/// <para>
/// It is registered on every host, including a Redis-off cell where the durable control plane
/// (<see cref="ControlPlaneEventHandler"/>, both backstop sweeps, and the Redis-backed tick
/// handlers) is not composed. Each call decides for itself: work that needs the durable stores
/// raises <see cref="ControlPlaneEventDependencyUnavailableException"/>, which the route turns into a
/// typed 503, and every other tick runs as usual.
/// </para>
/// </summary>
internal sealed class ControlPlaneLambdaEventTargets(
    IServiceProvider services,
    IServiceProviderIsService serviceCatalog,
    IOptions<ControlPlaneTriggerOptions> options) : IControlPlaneLambdaEventTargets
{
    /// <summary>
    /// Tick kinds whose handler is registered only with the Redis-backed durable stores: the workflow
    /// scheduler (orchestration engine), the job-reconciliation sweep (execution-job store and queue),
    /// and the geoprocessing output sweep (execution-job store).
    /// </summary>
    internal static readonly IReadOnlySet<ScheduledTickKind> DurableStoreTickKinds = new HashSet<ScheduledTickKind>
    {
        ScheduledTickKind.WorkflowSchedule,
        ScheduledTickKind.JobReconciliation,
        ScheduledTickKind.GeoprocessingOutputSweep,
    };

    /// <summary>Whether the Redis-backed durable control plane is composed on this host.</summary>
    internal bool DurableControlPlaneComposed => serviceCatalog.IsService(typeof(ControlPlaneEventHandler));

    public Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken)
    {
        EnsureDurableControlPlane();
        return services.GetRequiredService<ControlPlaneEventHandler>()
            .HandleExecutionJobEventAsync(providerOperationId, cancellationToken);
    }

    public async Task RunBackstopAsync(CancellationToken cancellationToken)
    {
        EnsureDurableControlPlane();
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

    public async Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<IScheduledTickDispatcher>()
                .RunTickAsync(kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException) when (DurableStoreTickKinds.Contains(kind) && !DurableControlPlaneComposed)
        {
            // No handler for a tick that only exists with the durable stores: name the missing
            // dependency rather than a generic "kind unavailable".
            throw new ControlPlaneEventDependencyUnavailableException(
                CapabilityUnavailableCodes.RedisDependency,
                kind.ToString());
        }
    }

    private void EnsureDurableControlPlane()
    {
        if (!DurableControlPlaneComposed)
        {
            throw new ControlPlaneEventDependencyUnavailableException(CapabilityUnavailableCodes.RedisDependency, subject: null);
        }
    }
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
