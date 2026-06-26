// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared base for the GeoETL <c>transform.*</c> executors. Each transform reads
/// features from the canonical <c>input</c> artifact, applies a NetTopologySuite
/// transformation that carries feature attributes through, and publishes a new
/// feature artifact. This is the #1185 add-a-capability shape: the executor is the
/// sole worker-side behavior for a single dotted process id and surfaces
/// automatically as a <c>process:&lt;id&gt;</c> workflow node.
///
/// <para>
/// <b>Streaming (ETL scale).</b> The base now runs a streamed execution path: the
/// input is opened as a lazy <see cref="IAsyncEnumerable{IFeature}"/> over the
/// upstream artifact (an inline base64 FeatureCollection <i>or</i> a spilled NDJSON
/// stream — see <see cref="FeatureStreamArtifact"/>), the transform processes it
/// chunk-by-chunk, and the output is published through
/// <see cref="FeatureStreamPublisher"/>, which keeps small results inline (back-compat)
/// but spills large results to an NDJSON file so peak memory stays bounded and the
/// legacy 50 MiB artifact cap no longer fails streamable transforms.
/// </para>
///
/// <para>
/// Transforms that <b>can</b> stream (per-feature maps/filters: attribute-rename,
/// attribute-cast, computed-field, attribute-filter, spatial-filter, clip, reproject)
/// override <see cref="ApplyStream"/>. Stateful transforms that need the full set in
/// memory (e.g. dedup with an in-memory key set) keep the buffered
/// <see cref="Apply(FeatureCollection, StepInputReader, CancellationToken)"/> path; the
/// base detects which is implemented and routes accordingly. Both paths publish through
/// the same spillable publisher, so even buffered transforms emit a streamed artifact
/// for large outputs rather than failing the cap.
/// </para>
/// </summary>
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

    /// <summary>
    /// The single dotted process id this executor handles (e.g. <c>transform.reproject</c>).
    /// </summary>
    protected abstract string ProcessId { get; }

    /// <inheritdoc />
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
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {inputError}");
        }

        // Open the input as a lazy feature stream. Spilled NDJSON streams are unbounded;
        // inline base64 data URIs remain bounded by MaxArtifactBytes for back-compat.
        if (!FeatureStreamArtifact.TryOpenRead(
                inputUri, out var parseError, out var inputStream, _options.CurrentValue.MaxArtifactBytes))
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: 'input' {parseError}");
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
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
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
                cancellationToken).ConfigureAwait(false);
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

        var output = Apply(source, inputs, cancellationToken);
        return await FeatureStreamPublisher.PublishAsync(
            ToAsync(output, cancellationToken),
            ProcessId,
            inputCount: source.Count,
            outputRoot,
            job.OperationId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<IFeature> ToAsync(
        IReadOnlyList<IFeature> features,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streaming transform entry point. Per-feature transforms (maps/filters) override this
    /// to process the input <see cref="IAsyncEnumerable{IFeature}"/> lazily and yield output
    /// features one at a time, so the full feature set is never materialized. The default
    /// returns <c>null</c>, signalling the base to fall back to the buffered
    /// <see cref="Apply(FeatureCollection, StepInputReader, CancellationToken)"/> path
    /// (for stateful transforms that genuinely need the whole collection). Throw
    /// <see cref="TransformInputException"/> for caller-supplied parameter errors.
    /// </summary>
    protected virtual IAsyncEnumerable<IFeature>? ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken) => null;

    /// <summary>
    /// Applies the transform's algorithm to the fully buffered input feature set.
    /// Implementations return the output features, preserving attributes. Used by stateful
    /// transforms that cannot stream; per-feature transforms should override
    /// <see cref="ApplyStream"/> instead. The default throws — a concrete executor must
    /// implement at least one of the two paths. Throw <see cref="TransformInputException"/>
    /// for caller-supplied parameter errors so they surface as a classified
    /// <c>Invalid ... inputs</c> failure.
    /// </summary>
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

/// <summary>
/// Signals a caller-supplied parameter error inside a transform body. Mapped by
/// <see cref="FeatureCollectionTransformExecutor"/> to a classified job failure.
/// </summary>
internal sealed class TransformInputException(string message) : Exception(message)
{
    public string PublicMessage { get; } = message;
}

/// <summary>
/// Thin reader over the durable spec parameter bag for the canonical first-step
/// (<c>0.</c>) inputs the geoprocessing submit path projects. Mirrors the prefix
/// the geometry executors read.
/// </summary>
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
}
