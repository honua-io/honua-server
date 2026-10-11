// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Thin AWS Lambda entrypoint for the cloud (event-driven) control-plane reconcile. Three handlers,
/// selected by the <c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER</c> environment variable so a single image
/// serves every EventBridge target:
/// <list type="bullet">
/// <item><c>batch-event</c>: deserializes an EventBridge "Batch Job State Change" event, extracts
/// <c>detail.jobId</c>, and drives one execution-job reconcile, then exits.</item>
/// <item><c>backstop</c>: invoked by EventBridge Scheduler on a coarse timer; runs one backstop
/// sweep (reconcile stale non-terminal ops) and exits.</item>
/// <item><c>scheduled-tick</c>: invoked by EventBridge Scheduler with <c>{"kind": "..."}</c>; runs
/// that periodic tick once and exits.</item>
/// </list>
/// An unrecognised selector fails the bootstrap at start-up rather than running the wrong handler.
/// The API image serves the same selector values in-process at the web adapter's <c>/events</c>
/// path (<c>ControlPlaneLambdaEventEndpoint</c> in Honua.Server).
/// Each invocation cold-starts (or warm-reuses), reconciles exactly once via the in-process
/// Honua.Server host (LWA), and returns — there is no always-on control-plane host.
/// </summary>
internal static class Function
{
    internal const string HandlerEnvVar = "HONUA_CONTROL_PLANE_LAMBDA_HANDLER";
    internal const string BatchEventHandler = "batch-event";
    internal const string BackstopHandler = "backstop";
    internal const string ScheduledTickHandler = "scheduled-tick";
    internal const string ReconcileHandlerAlias = "reconcile";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds()),
    };

    public static async Task Main()
    {
        var env = ReadEnvironment();
        var configured = env.GetValueOrDefault(HandlerEnvVar);
        var handlerMode = ResolveHandler(configured)
            ?? throw new InvalidOperationException(
                $"{HandlerEnvVar} '{configured}' is not one of {BatchEventHandler}, {ReconcileHandlerAlias}, "
                + $"{BackstopHandler}, {ScheduledTickHandler}.");
        var forwarder = new ReconcileForwarder(HttpClient, ReconcileForwarderOptions.FromEnvironment(env));
        var serializer = new SourceGeneratorLambdaJsonSerializer<ControlPlaneLambdaJsonContext>();

        if (handlerMode == ScheduledTickHandler)
        {
            Func<ScheduledTickEvent, ILambdaContext, Task> scheduledTick = async (evt, context) =>
            {
                context.Logger.LogInformation($"Control-plane scheduled tick {evt?.Kind}");
                using var cts = context.CreateDeadlineTokenSource();
                await RunScheduledTickAsync(forwarder, evt, cts.Token).ConfigureAwait(false);
            };

            using var tickBootstrap = LambdaBootstrapBuilder
                .Create(scheduledTick, serializer)
                .Build();
            await tickBootstrap.RunAsync().ConfigureAwait(false);
            return;
        }

        if (handlerMode == BackstopHandler)
        {
            // The Scheduler tick payload is opaque and ignored; the handler simply sweeps once.
            Func<BackstopTickEvent, ILambdaContext, Task> backstop = async (_, context) =>
            {
                context.Logger.LogInformation("Control-plane backstop sweep tick");
                using var cts = context.CreateDeadlineTokenSource();
                await forwarder.RunBackstopAsync(cts.Token).ConfigureAwait(false);
            };

            using var backstopBootstrap = LambdaBootstrapBuilder
                .Create(backstop, serializer)
                .Build();
            await backstopBootstrap.RunAsync().ConfigureAwait(false);
            return;
        }

        Func<BatchJobStateChangeEvent, ILambdaContext, Task> batchEvent = async (evt, context) =>
        {
            var providerOperationId = BatchEventParser.ExtractProviderOperationId(evt);
            if (string.IsNullOrWhiteSpace(providerOperationId))
            {
                context.Logger.LogInformation("Batch event carried no actionable jobId; nothing to reconcile");
                return;
            }

            context.Logger.LogInformation($"Reconciling execution job for Batch jobId {providerOperationId}");
            using var cts = context.CreateDeadlineTokenSource();
            await forwarder.ReconcileBatchEventAsync(providerOperationId, cts.Token).ConfigureAwait(false);
        };

        using var bootstrap = LambdaBootstrapBuilder
            .Create(batchEvent, serializer)
            .Build();
        await bootstrap.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Normalises the selector to one of the handler constants: unset means <c>batch-event</c> (the
    /// image default), <c>reconcile</c> is an alias for it, and anything unrecognised is
    /// <see langword="null"/>.
    /// </summary>
    internal static string? ResolveHandler(string? configured)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)
            || string.Equals(value, BatchEventHandler, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, ReconcileHandlerAlias, StringComparison.OrdinalIgnoreCase))
        {
            return BatchEventHandler;
        }

        if (string.Equals(value, BackstopHandler, StringComparison.OrdinalIgnoreCase))
        {
            return BackstopHandler;
        }

        return string.Equals(value, ScheduledTickHandler, StringComparison.OrdinalIgnoreCase)
            ? ScheduledTickHandler
            : null;
    }

    /// <summary>
    /// Forwards one scheduled tick. A payload without a kind is a mis-wired schedule, so it fails the
    /// invocation (visible in the function's error metric) instead of silently doing nothing.
    /// </summary>
    internal static Task RunScheduledTickAsync(
        ReconcileForwarder forwarder,
        ScheduledTickEvent? evt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forwarder);
        var kind = evt?.Kind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            throw new InvalidOperationException(
                "The scheduled-tick event carried no 'kind'; the schedule input must be {\"kind\": \"<ScheduledTickKind>\"}.");
        }

        return forwarder.RunScheduledTickAsync(kind, cancellationToken);
    }

    private static int GetTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("HONUA_CONTROL_PLANE_RECONCILE_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : 60;
    }

    private static Dictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value as string;
            }
        }

        return result;
    }
}

/// <summary>Helper extension to derive a cancellation token from a Lambda invocation's deadline.</summary>
internal static class LambdaContextExtensions
{
    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> that trips shortly before the Lambda
    /// deadline so a reconcile that overruns is cancelled cleanly rather than killed mid-flight by the
    /// runtime. The caller owns the returned source and must dispose it.
    /// </summary>
    public static CancellationTokenSource CreateDeadlineTokenSource(this ILambdaContext context)
    {
        var remaining = context.RemainingTime;
        if (remaining <= TimeSpan.Zero)
        {
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            return cancelled;
        }

        // Leave a small margin so the HTTP call is abandoned before the hard Lambda timeout.
        var budget = remaining - TimeSpan.FromMilliseconds(500);
        if (budget <= TimeSpan.Zero)
        {
            budget = remaining;
        }

        return new CancellationTokenSource(budget);
    }
}
