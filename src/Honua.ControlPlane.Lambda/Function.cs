// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Thin AWS Lambda entrypoint for the cloud (event-driven) control-plane reconcile. Two handlers,
/// selected by the <c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER</c> environment variable so a single image
/// serves both EventBridge targets:
/// <list type="bullet">
/// <item><c>batch-event</c>: deserializes an EventBridge "Batch Job State Change" event, extracts
/// <c>detail.jobId</c>, and drives one execution-job reconcile, then exits.</item>
/// <item><c>backstop</c>: invoked by EventBridge Scheduler on a coarse timer; runs one backstop
/// sweep (reconcile stale non-terminal ops) and exits.</item>
/// </list>
/// Each invocation cold-starts (or warm-reuses), reconciles exactly once via the in-process
/// Honua.Server host (LWA), and returns — there is no always-on control-plane host.
/// </summary>
internal static class Function
{
    internal const string HandlerEnvVar = "HONUA_CONTROL_PLANE_LAMBDA_HANDLER";
    internal const string BatchEventHandler = "batch-event";
    internal const string BackstopHandler = "backstop";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds()),
    };

    public static async Task Main()
    {
        var env = ReadEnvironment();
        var handlerMode = env.GetValueOrDefault(HandlerEnvVar) ?? BatchEventHandler;
        var forwarder = new ReconcileForwarder(HttpClient, ReconcileForwarderOptions.FromEnvironment(env));
        var serializer = new SourceGeneratorLambdaJsonSerializer<ControlPlaneLambdaJsonContext>();

        if (string.Equals(handlerMode, BackstopHandler, StringComparison.OrdinalIgnoreCase))
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
