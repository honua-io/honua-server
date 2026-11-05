// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

// The route exists only on an event-handler host and answers only loopback callers, so it needs no shared-secret token.
internal static partial class ControlPlaneLambdaEventEndpoint
{
    internal const string PassThroughPathKey = "AWS_LWA_PASS_THROUGH_PATH";

    internal const string DefaultPassThroughPath = "/events";

    internal const string BatchEventHandler = "batch-event";
    internal const string ReconcileHandlerAlias = "reconcile";
    internal const string BackstopHandler = "backstop";
    internal const string ScheduledTickHandler = "scheduled-tick";

    private const string ExpectedBatchSource = "aws.batch";
    private const string ExpectedBatchDetailType = "Batch Job State Change";

    internal sealed class ControlPlaneLambdaEventEndpointLog;

    internal static string ResolvePath(IConfiguration configuration)
    {
        var configured = configuration[PassThroughPathKey]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return DefaultPassThroughPath;
        }

        return configured.StartsWith('/') ? configured : "/" + configured;
    }

    // The route is not mapped on any other host, including the API function, so the public cannot reach it.
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

    // A present but unentitled Redis is reported as a licence problem; any other absence is a missing Redis.
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

internal sealed class ColdStartWarningLatch
{
    private readonly ConcurrentDictionary<string, byte> _entered = new(StringComparer.Ordinal);

    public static ColdStartWarningLatch Shared { get; } = new();

    public bool TryEnter(string key) => _entered.TryAdd(key, 0);
}

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

    public string MissingDependency { get; }

    public string? Subject { get; }
}

internal interface IControlPlaneLambdaEventTargets
{
    Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken);

    Task RunBackstopAsync(CancellationToken cancellationToken);

    Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken);
}

internal sealed class ControlPlaneLambdaEventTargets(
    IServiceProvider services,
    IServiceProviderIsService serviceCatalog,
    IOptions<ControlPlaneTriggerOptions> options) : IControlPlaneLambdaEventTargets
{
    internal static readonly IReadOnlySet<ScheduledTickKind> DurableStoreTickKinds = new HashSet<ScheduledTickKind>
    {
        ScheduledTickKind.WorkflowSchedule,
        ScheduledTickKind.JobReconciliation,
        ScheduledTickKind.GeoprocessingOutputSweep,
    };

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

internal sealed record ControlPlaneLambdaEventResult(
    [property: JsonPropertyName("handler")] string Handler,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("subject")] string? Subject);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ControlPlaneLambdaEventResult))]
[JsonSerializable(typeof(ControlPlaneEventProblem))]
internal sealed partial class ControlPlaneLambdaEventJsonContext : JsonSerializerContext;
