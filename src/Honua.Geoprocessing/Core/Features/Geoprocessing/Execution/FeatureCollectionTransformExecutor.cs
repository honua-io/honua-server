// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal abstract partial class FeatureCollectionTransformExecutor : IProcessExecutor
{
    private protected readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger _logger;
    private IReadOnlySet<string>? _processIds;

    protected FeatureCollectionTransformExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    // Compatibility overload for subclasses that do not inject an ILogger. The
    // NullLogger silences the log output; subclasses should prefer the two-arg
    // constructor when they receive ILogger from DI.
    protected FeatureCollectionTransformExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options)
        : this(options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
    }

    protected abstract string ProcessId { get; }

    public IReadOnlySet<string> ProcessIds =>
        _processIds ??= new HashSet<string>(StringComparer.Ordinal) { ProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(parameters);
        if (!string.Equals(resolved, ProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {ProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Parsing {ProcessId} inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.InvalidInput($"Invalid {ProcessId} inputs: {inputError}");
        }

        // Open the input as a lazy feature stream. Spilled NDJSON streams are unbounded;
        // inline base64 data URIs remain bounded by MaxArtifactBytes for back-compat.
        // Pass OutputRootDirectory so path-traversal injections in the reference are rejected.
        if (!FeatureStreamArtifact.TryOpenRead(
                inputUri, out var parseError, out var inputStream, _options.CurrentValue.MaxArtifactBytes,
                _options.CurrentValue.OutputRootDirectory, context))
        {
            return JobExecutionResult.InvalidInput($"Invalid {ProcessId} inputs: 'input' {parseError}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, $"Applying {ProcessId}", cancellationToken).ConfigureAwait(false);

        FeatureStreamPublisher.PublishResult published;
        try
        {
            published = await RunAsync(job, context, inputStream, inputs, cancellationToken).ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.InvalidInput($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.TransformComputationFailed(_logger, job.OperationId, ProcessId, ex);
            return JobExecutionResult.Failed($"{ProcessId} computation failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(published.ArtifactReference, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{ProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private async Task<FeatureStreamPublisher.PublishResult> RunAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        IAsyncEnumerable<IFeature> inputStream,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var outputRoot = _options.CurrentValue.OutputRootDirectory;

        // Streaming path: per-feature transforms process the input chunk-by-chunk and the
        // publisher spills large outputs. The full set is never held in memory.
        var streamed = ApplyStream(inputStream, inputs, cancellationToken);
        if (streamed is not null)
        {
            return await FeatureStreamPublisher.PublishAsync(
                streamed,
                ProcessId,
                inputCount: -1,
                outputRoot,
                job.OperationId,
                cancellationToken, executionContext: context).ConfigureAwait(false);
        }

        // Buffered path: stateful transforms (dedup) need the full collection. We still
        // drain the input incrementally and publish through the spillable publisher, so
        // large outputs stream out rather than failing the legacy artifact cap.
        var source = new FeatureCollection();
        await foreach (var feature in inputStream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Add(feature);
        }

        await context.ReportProgressAsync(60, $"Encoding {ProcessId} artifact", cancellationToken).ConfigureAwait(false);

        var output = await ApplyAsync(source, inputs, context, cancellationToken).ConfigureAwait(false);
        return await FeatureStreamPublisher.PublishAsync(
            FeatureStreamArtifact.ToAsync(output, cancellationToken),
            ProcessId,
            inputCount: source.Count,
            outputRoot,
            job.OperationId,
            cancellationToken, executionContext: context).ConfigureAwait(false);
    }

    protected virtual IAsyncEnumerable<IFeature>? ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken) => null;

    protected virtual Task<List<IFeature>> ApplyAsync(
        FeatureCollection source,
        StepInputReader inputs,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(Apply(source, inputs, cancellationToken));

    protected virtual List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{ProcessId} implements neither {nameof(ApplyStream)} nor {nameof(Apply)}.");

    private static partial class Log
    {
        [LoggerMessage(9230, LogLevel.Error,
            "Transform executor failed job {OperationId} during {ProcessId} computation")]
        public static partial void TransformComputationFailed(
            ILogger logger, string operationId, string processId, Exception exception);
    }
}

// The original exception is kept for logs and is never included in the message returned to the caller.
internal sealed class TransformInputException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string PublicMessage { get; } = message;
}

internal readonly struct StepInputReader(IReadOnlyDictionary<string, string> parameters)
{
    private readonly string _prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

    public bool TryGet(string name, out string? value)
    {
        if (parameters.TryGetValue(_prefix + name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryGetRequired(string name, out string value, out string error)
    {
        if (TryGet(name, out var raw))
        {
            value = raw!;
            error = "";
            return true;
        }

        value = "";
        error = $"missing required input '{name}'";
        return false;
    }

    public string Require(string name)
    {
        if (TryGet(name, out var value))
        {
            return value!;
        }

        throw new TransformInputException($"missing required input '{name}'");
    }

    public string GetOrDefault(string name, string defaultValue)
        => TryGet(name, out var value) ? value! : defaultValue;

    public int RequireSrid(string key)
    {
        if (!TryGet(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TransformInputException($"requires a positive integer '{key}' option.");
        }

        return value;
    }

    public bool ReadBool(string name, bool defaultValue)
    {
        if (!TryGet(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new TransformInputException($"'{name}' must be a boolean (true|false)"),
        };
    }

    public double ReadDistance()
    {
        if (!TryGet("distance", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            throw new TransformInputException("'distance' must be a finite non-negative number");
        }

        return value;
    }

    public double ReadUnitFactor()
    {
        var raw = GetOrDefault("unit", "meters").Trim().ToLowerInvariant();
        return raw switch
        {
            "" or "meters" or "meter" or "m" => 1.0,
            "kilometers" or "kilometer" or "km" => 1000.0,
            "feet" or "foot" or "ft" => 0.3048,
            "miles" or "mile" or "mi" => 1609.344,
            _ => throw new TransformInputException(
                $"unit '{raw}' is not supported (allowed: meters, kilometers, feet, miles)"),
        };
    }
}
