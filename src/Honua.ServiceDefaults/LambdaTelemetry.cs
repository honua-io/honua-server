// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Honua.ServiceDefaults;

public static class LambdaTelemetry
{
    public const string MeterName = "Honua.Lambda";

    private static readonly Meter Meter = new(MeterName, HonuaTelemetry.ServiceVersion);

    private static readonly Counter<long> ColdStartCounter = Meter.CreateCounter<long>(
        "honua.lambda.cold_start",
        unit: "{cold_start}",
        description: "Number of Lambda cold starts observed by the Honua process.");

    // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
    // Prometheus exporter derives the exported series name from the instrument name AND its unit:
    // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
    // out from under every dashboard and alert rule without breaking a single build. A PromQL query
    // against the absent name returns an empty vector, so the panel is blank and the alert never
    // fires, silently. Units are documented in the instrument name and description instead. See the
    // SLO-contract comment block in HonuaTelemetry.cs, observability/metric-name-contract.json, and
    // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

    private static readonly Histogram<double> InitDurationHistogram = Meter.CreateHistogram<double>(
        "honua.lambda.init_duration_ms",
        unit: null,
        description: "Duration of Lambda process initialization (cold start) in milliseconds.");

    private static int _coldStartRecorded;

    static LambdaTelemetry()
    {
        // Observable gauge for the configured memory limit. Tagged so it can be
        // correlated with AWS/Lambda's MaxMemoryUsed without re-deriving labels.
        Meter.CreateObservableGauge(
            "honua.lambda.memory_limit_mib",
            ObserveMemoryLimit,
            unit: null,
            description: "Configured Lambda memory limit in MiB (0 when not running on Lambda).");
    }

    public static LambdaContext Context { get; } = LambdaContext.FromEnvironment();

    public static bool IsRunningOnLambda => Context.IsLambda;

    public static string? FunctionName => Context.FunctionName;

    public static string? FunctionVersion => Context.FunctionVersion;

    public static string? InitializationType => Context.InitializationType;

    public static int MemoryLimitMib => Context.MemoryLimitMib;

    public static bool RecordColdStart(DateTime? processStartUtc = null)
    {
        if (!Context.IsLambda)
        {
            return false;
        }

        // Guard against double-counting if startup and a warmup path both call in.
        if (Interlocked.CompareExchange(ref _coldStartRecorded, 1, 0) != 0)
        {
            return false;
        }

        var initDurationMs = ResolveInitDurationMs(processStartUtc);
        EmitColdStart(Context, initDurationMs);
        return true;
    }

    public static void EmitColdStart(LambdaContext context, double initDurationMs)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tags = context.BuildTags();
        ColdStartCounter.Add(1, tags);

        if (initDurationMs >= 0)
        {
            InitDurationHistogram.Record(initDurationMs, tags);
        }
    }

    private static Measurement<long> ObserveMemoryLimit()
    {
        return new Measurement<long>(Context.MemoryLimitMib, Context.BuildTags());
    }

    private static double ResolveInitDurationMs(DateTime? processStartUtc)
    {
        DateTime start;
        try
        {
            start = processStartUtc
                ?? Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            // Process start time is unavailable in some sandboxed runtimes.
            return -1;
        }

        var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;

        // Clamp pathological clock-skew values rather than emit negatives.
        return elapsed < 0 ? -1 : elapsed;
    }
}

public sealed class LambdaContext
{
    public LambdaContext(
        string? functionName,
        string? functionVersion,
        string? initializationType,
        int memoryLimitMib)
    {
        FunctionName = functionName;
        FunctionVersion = functionVersion;
        InitializationType = initializationType;
        MemoryLimitMib = memoryLimitMib;
    }

    public string? FunctionName { get; }

    public string? FunctionVersion { get; }

    public string? InitializationType { get; }

    public int MemoryLimitMib { get; }

    public bool IsLambda => !string.IsNullOrEmpty(FunctionName)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT"));

    public static LambdaContext FromEnvironment()
    {
        var memoryRaw = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_MEMORY_SIZE");
        var memory = int.TryParse(memoryRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

        return new LambdaContext(
            Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"),
            Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_VERSION"),
            Environment.GetEnvironmentVariable("AWS_LAMBDA_INITIALIZATION_TYPE"),
            memory);
    }

    public KeyValuePair<string, object?>[] BuildTags()
    {
        return
        [
            new KeyValuePair<string, object?>("function.name", FunctionName ?? "unknown"),
            new KeyValuePair<string, object?>("function.version", FunctionVersion ?? "unknown"),
            new KeyValuePair<string, object?>("init.type", InitializationType ?? "on-demand"),
            new KeyValuePair<string, object?>("memory.limit_mib", MemoryLimitMib),
        ];
    }
}
