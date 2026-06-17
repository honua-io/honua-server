// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Honua.ServiceDefaults;

/// <summary>
/// Emits AWS Lambda cold-start and runtime metrics through a dedicated meter
/// (<see cref="MeterName"/>) that the shared OpenTelemetry pipeline registers,
/// so they flow to the existing Prometheus and OTLP exporters without any
/// additional wiring.
/// </summary>
/// <remarks>
/// Honua runs on Lambda behind the AWS Lambda Web Adapter, so the host is a
/// long-lived ASP.NET process: the cold start is the process initialization
/// (an invocation reusing a warm execution environment skips it). This type
/// observes the standard <c>AWS_LAMBDA_*</c> environment variables that the
/// Lambda runtime injects and records:
/// <list type="bullet">
///   <item><description><c>honua.lambda.cold_start</c> — counter incremented once per cold start.</description></item>
///   <item><description><c>honua.lambda.init_duration_ms</c> — histogram of process initialization duration.</description></item>
///   <item><description><c>honua.lambda.memory_limit_mib</c> — observable gauge of the configured Lambda memory limit.</description></item>
/// </list>
/// All instruments are tagged with the function name, version and init type so
/// they slice cleanly alongside the AWS/Lambda namespace metrics in CloudWatch
/// and the custom Honua metrics namespace.
/// </remarks>
public static class LambdaTelemetry
{
    /// <summary>
    /// The meter name registered with OpenTelemetry for Lambda runtime metrics.
    /// </summary>
    public const string MeterName = "Honua.Lambda";

    private static readonly Meter Meter = new(MeterName, HonuaTelemetry.ServiceVersion);

    private static readonly Counter<long> ColdStartCounter = Meter.CreateCounter<long>(
        "honua.lambda.cold_start",
        unit: "{cold_start}",
        description: "Number of Lambda cold starts observed by the Honua process.");

    private static readonly Histogram<double> InitDurationHistogram = Meter.CreateHistogram<double>(
        "honua.lambda.init_duration_ms",
        unit: "ms",
        description: "Duration of Lambda process initialization (cold start) in milliseconds.");

    private static int _coldStartRecorded;

    static LambdaTelemetry()
    {
        // Observable gauge for the configured memory limit. Tagged so it can be
        // correlated with AWS/Lambda's MaxMemoryUsed without re-deriving labels.
        Meter.CreateObservableGauge(
            "honua.lambda.memory_limit_mib",
            ObserveMemoryLimit,
            unit: "MiB",
            description: "Configured Lambda memory limit in MiB (0 when not running on Lambda).");
    }

    /// <summary>
    /// Gets the Lambda context resolved from the process environment, used by
    /// the production cold-start recording path.
    /// </summary>
    public static LambdaContext Context { get; } = LambdaContext.FromEnvironment();

    /// <summary>
    /// Gets a value indicating whether the process is running inside the AWS
    /// Lambda runtime, detected via the runtime-injected environment variables.
    /// </summary>
    public static bool IsRunningOnLambda => Context.IsLambda;

    /// <summary>
    /// Gets the Lambda function name, or <see langword="null"/> when not on Lambda.
    /// </summary>
    public static string? FunctionName => Context.FunctionName;

    /// <summary>
    /// Gets the Lambda function version, or <see langword="null"/> when not on Lambda.
    /// </summary>
    public static string? FunctionVersion => Context.FunctionVersion;

    /// <summary>
    /// Gets the Lambda initialization type (for example <c>on-demand</c> or
    /// <c>provisioned-concurrency</c>), or <see langword="null"/> when not on Lambda.
    /// </summary>
    public static string? InitializationType => Context.InitializationType;

    /// <summary>
    /// Gets the configured Lambda memory limit in MiB, or <c>0</c> when not on Lambda.
    /// </summary>
    public static int MemoryLimitMib => Context.MemoryLimitMib;

    /// <summary>
    /// Records the cold start exactly once for the lifetime of the process.
    /// Subsequent calls are no-ops, so this is safe to invoke from startup and
    /// from a warmup handler. When not running on Lambda the call is ignored.
    /// </summary>
    /// <param name="processStartUtc">
    /// The UTC timestamp the process began initializing. Defaults to the OS
    /// process start time, which captures the full cold-start window.
    /// </param>
    /// <returns><see langword="true"/> when this call recorded the cold start.</returns>
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

    /// <summary>
    /// Emits the cold-start counter and init-duration histogram for the supplied
    /// context. Exposed for deterministic unit testing of metric emission; the
    /// production path goes through <see cref="RecordColdStart(DateTime?)"/>.
    /// </summary>
    /// <param name="context">The Lambda context whose tags are applied.</param>
    /// <param name="initDurationMs">
    /// Initialization duration in milliseconds, or a negative value to skip the
    /// histogram sample.
    /// </param>
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

/// <summary>
/// Immutable snapshot of the AWS Lambda execution context derived from the
/// runtime-injected environment variables. Constructing one explicitly makes
/// the cold-start metric emission deterministically unit-testable without
/// mutating process-global environment state.
/// </summary>
public sealed class LambdaContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LambdaContext"/> class.
    /// </summary>
    /// <param name="functionName">The Lambda function name, if present.</param>
    /// <param name="functionVersion">The Lambda function version, if present.</param>
    /// <param name="initializationType">The Lambda initialization type, if present.</param>
    /// <param name="memoryLimitMib">The configured memory limit in MiB (0 when unknown).</param>
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

    /// <summary>Gets the Lambda function name, or <see langword="null"/> off Lambda.</summary>
    public string? FunctionName { get; }

    /// <summary>Gets the Lambda function version, or <see langword="null"/> off Lambda.</summary>
    public string? FunctionVersion { get; }

    /// <summary>Gets the Lambda initialization type, or <see langword="null"/> off Lambda.</summary>
    public string? InitializationType { get; }

    /// <summary>Gets the configured memory limit in MiB (0 when unknown).</summary>
    public int MemoryLimitMib { get; }

    /// <summary>
    /// Gets a value indicating whether this context represents an active Lambda
    /// execution environment (a function name is present).
    /// </summary>
    public bool IsLambda => !string.IsNullOrEmpty(FunctionName)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT"));

    /// <summary>
    /// Builds a <see cref="LambdaContext"/> from the current process environment.
    /// </summary>
    /// <returns>The resolved context.</returns>
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

    /// <summary>
    /// Builds the common tag set applied to every Lambda instrument.
    /// </summary>
    /// <returns>The tag array (function name, version, init type, memory limit).</returns>
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
