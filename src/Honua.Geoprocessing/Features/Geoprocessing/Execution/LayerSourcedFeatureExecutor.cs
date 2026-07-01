// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared base for the <b>layer-aware</b> managed vector operations that execute as a
/// SINGLE dispatched geoprocessing job over a Honua catalog layer (#2322, #2325).
///
/// <para>
/// The geoprocessing job runtime is single-step: a dispatched job runs exactly one
/// executor reading the plan's step-0 inputs (there is no in-job step chaining — a
/// <c>source.honua-layer -&gt; op -&gt; sink</c> DAG only runs through the workflow
/// orchestration engine as one child job per node). The per-operation OGC API -
/// Processes projections (#1382) for the layer-scoped analytics/generalization/
/// conversion ops advertise a <c>layerId</c> input and are submitted as a single
/// job, so they cannot consume an inline FeatureCollection produced by a separate
/// upstream node. This base closes that gap by fusing the layer read with the
/// managed op in one executor: it resolves the <c>source.honua-layer</c>
/// <see cref="IDagFeatureSource"/> (REUSING the canonical query pipeline through the
/// same provider-resolution pattern as <see cref="RemoteSourceExecutor"/>), streams
/// the layer's features, hands them to the concrete op, and publishes the canonical
/// FeatureCollection artifact — mirroring how the inline <c>overlay.*</c> layer-aware
/// executors emit their result, but sourcing the input layer itself.
/// </para>
///
/// <para>
/// In a lean, catalog-free deployment no <c>source.honua-layer</c> connector is
/// registered, so the executor fails closed with a clear, actionable message rather
/// than taking a Postgres dependency in the dispatcher assembly.
/// </para>
/// </summary>
internal abstract partial class LayerSourcedFeatureExecutor : IProcessExecutor
{
    /// <summary>The <see cref="IDagFeatureSource.SourceId"/> this base reads from.</summary>
    private protected const string HonuaLayerSourceId = "source.honua-layer";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;
    private IReadOnlySet<string>? _processIds;

    private protected LayerSourcedFeatureExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        Options = options;
        _logger = logger;
    }

    /// <summary>The single dotted process id this executor handles (e.g. <c>analytics.buffer-aggregate</c>).</summary>
    protected abstract string ProcessId { get; }

    /// <summary>Shared executor options (artifact caps, output roots).</summary>
    private protected IOptionsMonitor<GeoprocessingExecutorOptions> Options { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds =>
        _processIds ??= new HashSet<string>(StringComparer.Ordinal) { ProcessId };

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, ProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {ProcessId} executor.");
        }

        var inputs = new StepInputReader(job.Spec.Parameters);

        DagSourceRequest request;
        try
        {
            request = BuildSourceRequest(inputs);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Resolving {ProcessId} source layer", cancellationToken).ConfigureAwait(false);

        using var scope = _serviceScopeFactory.CreateScope();
        var source = ResolveHonuaLayerSource(scope.ServiceProvider);
        if (source is null)
        {
            Log.SourceUnavailable(_logger, job.OperationId, ProcessId);
            return JobExecutionResult.Failed(
                $"The {ProcessId} process is unavailable in this deployment: it reads a Honua catalog layer " +
                $"through the {HonuaLayerSourceId} connector, which is not configured here.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, $"Streaming layer features for {ProcessId}", cancellationToken).ConfigureAwait(false);

        List<IFeature> features;
        try
        {
            features = await ReadLayerAsync(source, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SourceReadFailed(_logger, job.OperationId, ProcessId, ex);
            return JobExecutionResult.Failed($"{ProcessId} failed reading the source layer: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(55, $"Applying {ProcessId}", cancellationToken).ConfigureAwait(false);

        List<IFeature> output;
        try
        {
            output = await ApplyCoreAsync(new LayerOpContext(features, source), inputs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ComputationFailed(_logger, job.OperationId, ProcessId, ex);
            return JobExecutionResult.Failed($"{ProcessId} computation failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(80, $"Encoding {ProcessId} artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(output, ProcessId);
        var maxBytes = Options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{ProcessId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{ProcessId} completed ({output.Count} features)", cancellationToken)
            .ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    /// <summary>
    /// Applies the concrete layer-aware operation to the streamed source features,
    /// returning the output feature set. Throw <see cref="TransformInputException"/>
    /// for caller-supplied parameter errors so they surface as a classified
    /// <c>Invalid ... inputs</c> failure.
    ///
    /// <para>
    /// Single-layer ops override this synchronous hook. A two-layer op (for example
    /// <c>analytics.spatial-join</c>, which resolves a second catalog layer through
    /// the same <c>source.honua-layer</c> connector) overrides
    /// <see cref="ApplyCoreAsync"/> instead and leaves this default in place.
    /// </para>
    /// </summary>
    protected virtual List<IFeature> Apply(
        List<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{GetType().Name} must override {nameof(Apply)} or {nameof(ApplyCoreAsync)}.");

    /// <summary>
    /// Asynchronous apply seam. The default delegates to the synchronous
    /// <see cref="Apply"/> over the already-streamed target layer. A two-layer op
    /// overrides this to resolve and read an additional catalog layer (via
    /// <see cref="LayerOpContext.LayerSource"/> and <see cref="ReadLayerAsync"/>)
    /// before computing its output. Throw <see cref="TransformInputException"/> for
    /// caller-supplied parameter errors so they surface as a classified
    /// <c>Invalid ... inputs</c> failure.
    /// </summary>
    private protected virtual Task<List<IFeature>> ApplyCoreAsync(
        LayerOpContext context,
        StepInputReader inputs,
        CancellationToken cancellationToken)
        => Task.FromResult(Apply(context.Features, inputs, cancellationToken));

    /// <summary>
    /// The resolved inputs a concrete layer-aware op computes over: the streamed
    /// target-layer <see cref="Features"/> and the resolved <see cref="LayerSource"/>
    /// connector, so a two-layer op can read a second catalog layer through the same
    /// connector within the executor's service scope.
    /// </summary>
    private protected readonly struct LayerOpContext
    {
        /// <summary>Creates a context over the streamed target features and connector.</summary>
        public LayerOpContext(List<IFeature> features, IDagFeatureSource layerSource)
        {
            Features = features;
            LayerSource = layerSource;
        }

        /// <summary>The streamed features of the primary (target) layer.</summary>
        public List<IFeature> Features { get; }

        /// <summary>The resolved <c>source.honua-layer</c> connector for further layer reads.</summary>
        public IDagFeatureSource LayerSource { get; }
    }

    /// <summary>
    /// Streams every feature the resolved <paramref name="source"/> returns for
    /// <paramref name="request"/> into an in-memory NetTopologySuite feature list.
    /// Shared by the base's primary layer read and by two-layer ops that resolve a
    /// second catalog layer through the same connector.
    /// </summary>
    private protected static async Task<List<IFeature>> ReadLayerAsync(
        IDagFeatureSource source,
        DagSourceRequest request,
        CancellationToken cancellationToken)
    {
        var geoJsonReader = new GeoJsonReader();
        var features = new List<IFeature>();
        await foreach (var sourceFeature in source.ReadAsync(request, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            features.Add(ToNtsFeature(sourceFeature, geoJsonReader));
        }

        return features;
    }

    /// <summary>
    /// Reads and validates a non-negative catalog layer id from the input at
    /// <paramref name="key"/> (for example <c>layerId</c> for the target layer or
    /// <c>joinLayerId</c> for a spatial-join reference layer).
    /// </summary>
    private protected static int RequireLayerId(StepInputReader inputs, string key)
    {
        if (!inputs.TryGet(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw new TransformInputException(
                $"missing or invalid required input '{key}'; expected a non-negative integer.");
        }

        return value;
    }

    /// <summary>
    /// Hook for a concrete op to add operation-specific fields to the layer read
    /// request (for example <c>conversion.feature-project</c> requesting server-side
    /// reprojection to the target SRID via <see cref="DagSourceRequest.OutputSrid"/>).
    /// The base has already populated the common windowing fields. The default is a
    /// no-op. Throw <see cref="TransformInputException"/> for invalid inputs.
    /// </summary>
    protected virtual DagSourceRequest CustomizeRequest(DagSourceRequest request, StepInputReader inputs)
        => request;

    private DagSourceRequest BuildSourceRequest(StepInputReader inputs)
    {
        var request = new DagSourceRequest
        {
            LayerId = RequireLayerId(inputs, "layerId"),
            Where = inputs.TryGet("where", out var where) ? where : null,
            Bbox = inputs.TryGet("bbox", out var bbox) ? bbox : null,
            OutFields = inputs.TryGet("outFields", out var outFields) ? outFields : null,
            OutputSrid = TryGetPositiveInt(inputs, "outSrid"),
            Since = inputs.TryGet("since", out var since) ? since : null,
            WatermarkField = inputs.TryGet("watermarkField", out var watermarkField) ? watermarkField : null,
        };

        return CustomizeRequest(request, inputs);
    }

    private static IDagFeatureSource? ResolveHonuaLayerSource(IServiceProvider services)
    {
        foreach (var candidate in services.GetServices<IDagFeatureSource>())
        {
            if (string.Equals(candidate.SourceId, HonuaLayerSourceId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static Feature ToNtsFeature(DagSourceFeature sourceFeature, GeoJsonReader reader)
    {
        NtsGeometry? geometry = null;
        if (!string.IsNullOrWhiteSpace(sourceFeature.GeometryGeoJson))
        {
            try
            {
                geometry = reader.Read<NtsGeometry>(sourceFeature.GeometryGeoJson);
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException)
            {
                geometry = null;
            }
        }

        var attributes = new AttributesTable();
        foreach (var (key, value) in sourceFeature.Attributes)
        {
            if (!attributes.Exists(key))
            {
                attributes.Add(key, value);
            }
        }

        return new Feature(geometry, attributes);
    }

    private protected static int? TryGetPositiveInt(StepInputReader inputs, string name)
        => inputs.TryGet(name, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : null;

    private static partial class Log
    {
        [LoggerMessage(9300, LogLevel.Warning,
            "Layer-sourced executor refused job {OperationId} for {ProcessId}: no source.honua-layer connector is registered")]
        public static partial void SourceUnavailable(ILogger logger, string operationId, string processId);

        [LoggerMessage(9301, LogLevel.Error,
            "Layer-sourced executor failed job {OperationId} during {ProcessId} layer read")]
        public static partial void SourceReadFailed(ILogger logger, string operationId, string processId, Exception exception);

        [LoggerMessage(9302, LogLevel.Error,
            "Layer-sourced executor failed job {OperationId} during {ProcessId} computation")]
        public static partial void ComputationFailed(ILogger logger, string operationId, string processId, Exception exception);
    }
}
