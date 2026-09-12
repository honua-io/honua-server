// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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

    /// <summary>
    /// A provable lower bound on the GeoJSON bytes one coordinate occupies: <c>[x,y]</c> is at
    /// least five characters even with single-digit ordinates, before any separator (#4629).
    /// </summary>
    private protected const long MinSerializedBytesPerVertex = 5;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;
    private IReadOnlySet<string>? _processIds;

    private protected LayerSourcedFeatureExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger logger,
        IOptions<LimitsOptions>? limitsOptions = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        Options = options;
        _logger = logger;
        // Reuse the SAME canonical admission limits the synchronous SpatialAnalytics/
        // DataEnrichment request handlers already enforce (AnalyticsLimits.MaxInputFeatures)
        // and the same per-geometry vertex ceiling FeatureServer edits enforce
        // (GeometryLimits.MaxVerticesPerGeometry), rather than inventing a GP-local budget
        // (#4629): a dispatched layer-sourced job and its synchronous sibling now fail at
        // the same admission point instead of the job path materializing what the
        // synchronous path would have already refused.
        Limits = limitsOptions?.Value ?? new LimitsOptions();
    }

    /// <summary>The single dotted process id this executor handles (e.g. <c>analytics.buffer-aggregate</c>).</summary>
    protected abstract string ProcessId { get; }

    /// <summary>Shared executor options (artifact caps, output roots).</summary>
    private protected IOptionsMonitor<GeoprocessingExecutorOptions> Options { get; }

    /// <summary>
    /// Canonical system limits (input feature counts, per-geometry vertex ceilings) shared
    /// with the synchronous SpatialAnalytics/DataEnrichment request handlers and FeatureServer
    /// edit validation, so a dispatched GP job admits input under the same budget its
    /// synchronous sibling would (#4629).
    /// </summary>
    private protected LimitsOptions Limits { get; }

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seconds = Options.CurrentValue.MaxLayerExecutionSeconds;
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await ExecuteCoreAsync(job, context, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return JobExecutionResult.Failed(
                $"{ProcessId} exceeded Geoprocessing:Executors:MaxLayerExecutionSeconds={seconds}; " +
                "narrow the selection or simplify the input, then resubmit.");
        }
    }

    private async Task<JobExecutionResult> ExecuteCoreAsync(
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

        try
        {
            await ValidateStatisticsAsync(scope.ServiceProvider, inputs, request, cancellationToken).ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, $"Streaming layer features for {ProcessId}", cancellationToken).ConfigureAwait(false);

        List<IFeature> features;
        try
        {
            // Bound the target layer read by the same canonical admission limits the
            // synchronous analytics/enrichment handlers already enforce (#4629): the read
            // fails closed WHILE STREAMING on the first oversized feature count or
            // oversized single geometry, instead of materializing, computing, and
            // serializing an unbounded input before MaxArtifactBytes is ever checked.
            features = await ReadLayerAsync(
                    source,
                    request,
                    cancellationToken,
                    Limits.Analytics.MaxInputFeatures,
                    $"layer {request.LayerId}",
                    Limits.Geometry.MaxVerticesPerGeometry,
                    Limits.Geometry.MaxGeometrySize,
                    Limits.Analytics.MaxInputBytes,
                    Options.CurrentValue.MaxLayerVertices)
                .ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            // The feature/vertex-count budgets surface here with concrete remedies (narrow
            // where/bbox, raise the configured limit), so the message must reach the caller
            // verbatim rather than collapsing to a bare exception type name.
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.PublicMessage}");
        }
        catch (DagSourceSelectionException ex)
        {
            // A geometry/time selector the canonical translator rejected (or could not
            // evaluate in this deployment): caller-facing, so surface it verbatim (#4624).
            return JobExecutionResult.Failed($"Invalid {ProcessId} inputs: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: any source-read failure must become a Failed job
            // result rather than crash the worker; the full exception is logged and
            // only the exception type name reaches the result.
            Log.SourceReadFailed(_logger, job.OperationId, ProcessId, ex);
            return JobExecutionResult.Failed($"{ProcessId} failed reading the source layer: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(55, $"Applying {ProcessId}", cancellationToken).ConfigureAwait(false);

        List<IFeature> output;
        try
        {
            output = await ApplyCoreAsync(new LayerOpContext(features, source, scope.ServiceProvider), inputs, cancellationToken)
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

        // #4629: serialization allocates the GeoJSON text, a parsed copy and the re-emitted
        // payload, so checking MaxArtifactBytes only afterwards let an oversized output cost
        // several times its size before failing. Refuse up front when even the smallest possible
        // encoding of the output's coordinates cannot fit; the post-serialization check below
        // still covers attributes and real ordinate widths.
        var maxBytes = Options.CurrentValue.MaxArtifactBytes;
        var outputVertices = output.Sum(feature => (long)(feature.Geometry?.NumPoints ?? 0));
        if (outputVertices * MinSerializedBytesPerVertex > maxBytes)
        {
            await context.ReportProgressAsync(80, $"{ProcessId} stopped: output exceeds the artifact budget", cancellationToken)
                .ConfigureAwait(false);
            return JobExecutionResult.Failed(
                $"{ProcessId} output has {outputVertices} vertices across {output.Count} features, which needs at least " +
                $"{outputVertices * MinSerializedBytesPerVertex} bytes once serialized and exceeds the configured " +
                $"MaxArtifactBytes={maxBytes}; stopped before serialization. Narrow the selection (where/objectIds/geometry/time), " +
                "simplify the input, or raise Geoprocessing:Executor:MaxArtifactBytes, then resubmit.");
        }

        await context.ReportProgressAsync(80, $"Encoding {ProcessId} artifact", cancellationToken).ConfigureAwait(false);

        byte[] payload;
        try
        {
            payload = BoundedArtifactWriter.WriteFeatureCollection(output, ProcessId, maxBytes, cancellationToken,
                request.OutputSrid is { } outputSrid ? [("srid", outputSrid)] : null);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"{ProcessId} {ex.PublicMessage}");
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
    /// target-layer <see cref="Features"/>, the resolved <see cref="LayerSource"/>
    /// connector (so a two-layer op can read a second catalog layer through the same
    /// connector), and the executor's request-scoped <see cref="Services"/> provider (so
    /// an op can resolve additional shared services — for example the CRS-aware
    /// <c>IGeometryOperationService</c> or catalog metadata provider — within the same
    /// scope <see cref="LayerSource"/> was resolved from).
    /// </summary>
    private protected readonly struct LayerOpContext
    {
        /// <summary>Creates a context over the streamed target features, connector, and scope.</summary>
        public LayerOpContext(List<IFeature> features, IDagFeatureSource layerSource, IServiceProvider services)
        {
            Features = features;
            LayerSource = layerSource;
            Services = services;
        }

        /// <summary>The streamed features of the primary (target) layer.</summary>
        public List<IFeature> Features { get; }

        /// <summary>The resolved <c>source.honua-layer</c> connector for further layer reads.</summary>
        public IDagFeatureSource LayerSource { get; }

        /// <summary>The executor's request-scoped service provider.</summary>
        public IServiceProvider Services { get; }
    }

    /// <summary>
    /// Streams every feature the resolved <paramref name="source"/> returns for
    /// <paramref name="request"/> into an in-memory NetTopologySuite feature list.
    /// Shared by the base's primary layer read, by two-layer ops that resolve a
    /// second catalog layer through the same connector, and by the standalone
    /// <c>enrichment.enrich</c> executor (#2283).
    ///
    /// <para>
    /// The optional <paramref name="maxVerticesPerGeometry"/> charges a per-geometry vertex
    /// ceiling independently of <paramref name="maxFeatures"/> (#4629): a feature-count cap
    /// alone does not bound a single deliberately oversized geometry (one feature, millions of
    /// vertices), so this fails closed as soon as the oversized geometry streams in.
    /// </para>
    ///
    /// <para>
    /// <paramref name="maxGeometryBytes"/> and <paramref name="maxInputBytes"/> charge the
    /// serialized size of each geometry and the cumulative geometry+attribute payload (#4629)
    /// BEFORE the geometry is parsed into NetTopologySuite objects, so neither a single huge
    /// geometry nor a moderate count of large features is materialized past its budget.
    /// </para>
    /// </summary>
    internal static async Task<List<IFeature>> ReadLayerAsync(
        IDagFeatureSource source,
        DagSourceRequest request,
        CancellationToken cancellationToken,
        int? maxFeatures = null,
        string? limitLabel = null,
        int? maxVerticesPerGeometry = null,
        long? maxGeometryBytes = null,
        long? maxInputBytes = null,
        int? maxTotalVertices = null)
    {
        var geoJsonReader = new GeoJsonReader();
        var features = new List<IFeature>();
        long inputBytes = 0;
        long totalVertices = 0;
        await foreach (var sourceFeature in source.ReadAsync(request, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Bounded admission: fail fast WHILE streaming rather than materializing an
            // unbounded layer and only discovering the size after the whole feature set,
            // the output set, and the serialized artifact have been allocated.
            if (maxFeatures is { } cap && features.Count >= cap)
            {
                throw new TransformInputException(
                    $"{limitLabel ?? "layer"} exceeds the configured limit of {cap} features; "
                    + "narrow the selection (where/bbox) or raise the limit.");
            }

            long geometryBytes = sourceFeature.GeometryGeoJson?.Length ?? 0;
            if (maxGeometryBytes is { } geometryCap && geometryBytes > geometryCap)
            {
                throw new TransformInputException(
                    $"{limitLabel ?? "layer"} contains a geometry of {geometryBytes} serialized bytes, exceeding the "
                    + $"configured limit of {geometryCap} bytes (Limits:Geometry:MaxGeometrySize); simplify the source "
                    + "geometry or raise the limit.");
            }

            inputBytes += geometryBytes + EstimateAttributeBytes(sourceFeature.Attributes);
            if (maxInputBytes is { } inputCap && inputBytes > inputCap)
            {
                throw new TransformInputException(
                    $"{limitLabel ?? "layer"} exceeds the configured input budget of {inputCap} bytes "
                    + $"(Limits:Analytics:MaxInputBytes) at feature {features.Count + 1}; narrow the selection "
                    + "(where/objectIds/geometry/time) or raise the limit.");
            }

            var feature = ToNtsFeature(sourceFeature, geoJsonReader);

            if (maxVerticesPerGeometry is { } vertexCap
                && feature.Geometry is { } geometry
                && geometry.NumPoints > vertexCap)
            {
                throw new TransformInputException(
                    $"{limitLabel ?? "layer"} contains a geometry with {geometry.NumPoints} vertices, "
                    + $"exceeding the configured limit of {vertexCap}; simplify the source geometry or "
                    + "raise the limit.");
            }

            totalVertices += feature.Geometry?.NumPoints ?? 0;
            if (maxTotalVertices is { } totalCap && totalVertices > totalCap)
            {
                throw new TransformInputException(
                    $"{limitLabel ?? "layer"} exceeds Geoprocessing:Executors:MaxLayerVertices={totalCap} " +
                    "while streaming; narrow the selection or simplify the input, then resubmit.");
            }

            features.Add(feature);
        }

        return features;
    }

    /// <summary>
    /// Serialized-size estimate of a streamed feature's attributes for the input byte budget:
    /// key and string lengths as written, a fixed width for scalars.
    /// </summary>
    private static long EstimateAttributeBytes(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        long bytes = 0;
        foreach (var (key, value) in attributes)
        {
            bytes += key.Length + value switch
            {
                null => 4,
                string text => text.Length + 2,
                byte[] blob => blob.Length,
                _ => 8,
            };
        }

        return bytes;
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

    // The esriGeometryType vocabulary the canonical GeoServices geometry parser accepts, and
    // the distance-based relationships the analytics surface rejects (ProcessPlanValidator and
    // AnalyticsFeatureQueryFactory apply the same rules). Checked here only so a malformed
    // selector fails before any layer read; the canonical translator stays authoritative.
    private static readonly HashSet<string> KnownGeometryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "esriGeometryPoint", "esriGeometryMultipoint", "esriGeometryPolyline", "esriGeometryPolygon", "esriGeometryEnvelope",
    };

    private static readonly HashSet<string> DistanceSpatialRelationships = new(StringComparer.OrdinalIgnoreCase)
    {
        "esriSpatialRelWithinDistance", "esriSpatialRelBeyondDistance",
    };

    private DagSourceRequest BuildSourceRequest(StepInputReader inputs)
    {
        // #4624: the catalog advertises objectIds / geometry+geometryType+inSR+spatialRel /
        // time+timeRelation on every op that includes SharedAnalyticsFilterParameters
        // (analytics.buffer-aggregate, analytics.spatial-join, generalization.dissolve,
        // generalization.simplify-layer). Every selector is propagated verbatim to
        // source.honua-layer, which interprets geometry/time through the SAME canonical
        // translation the synchronous analytics endpoints use (ILayerSelectionFilterTranslator)
        // and fails closed when it cannot — never a connector-local approximation.
        var bbox = ReadNonBlank(inputs, "bbox");
        var geometry = ReadNonBlank(inputs, "geometry");
        if (bbox is not null && geometry is not null)
        {
            throw new TransformInputException("supply either 'bbox' or 'geometry', not both.");
        }

        var geometryType = ReadNonBlank(inputs, "geometryType");
        var spatialRel = ReadNonBlank(inputs, "spatialRel");
        if (geometry is not null)
        {
            if (geometryType is not null && !KnownGeometryTypes.Contains(geometryType.Trim()))
            {
                throw new TransformInputException(
                    $"'geometryType' '{geometryType}' is not supported (supported: esriGeometryPoint, " +
                    "esriGeometryMultipoint, esriGeometryPolyline, esriGeometryPolygon, esriGeometryEnvelope).");
            }

            if (spatialRel is not null && DistanceSpatialRelationships.Contains(spatialRel.Trim()))
            {
                throw new TransformInputException(
                    $"'spatialRel' '{spatialRel}' is distance-based, which the analytics operations do not support; " +
                    "use the operation-specific 'distance' parameter or apply the predicate through 'where' instead.");
            }
        }

        var time = ReadNonBlank(inputs, "time");
        var request = new DagSourceRequest
        {
            LayerId = RequireLayerId(inputs, "layerId"),
            Where = inputs.TryGet("where", out var where) ? where : null,
            Bbox = bbox,
            ObjectIds = ResolveObjectIds(inputs),
            Geometry = geometry,
            GeometryType = geometry is null ? null : geometryType,
            InSr = geometry is null ? null : ReadNonBlank(inputs, "inSR"),
            SpatialRel = geometry is null ? null : spatialRel,
            Time = time,
            TimeRelation = time is null ? null : ReadNonBlank(inputs, "timeRelation"),
            OutFields = inputs.TryGet("outFields", out var outFields) ? outFields : null,
            OutputSrid = TryGetPositiveInt(inputs, "outSrid"),
            Since = inputs.TryGet("since", out var since) ? since : null,
            WatermarkField = inputs.TryGet("watermarkField", out var watermarkField) ? watermarkField : null,
        };

        return CustomizeRequest(request, inputs);
    }

    private static string? ReadNonBlank(StepInputReader inputs, string key)
        => inputs.TryGet(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;

    /// <summary>
    /// The catalog layer whose schema an <c>outStatistics</c> request aggregates over. The
    /// target layer by default; a two-layer op that aggregates matched rows of a second layer
    /// (<c>analytics.spatial-join</c>) overrides this.
    /// </summary>
    private protected virtual int? ResolveStatisticsLayerId(StepInputReader inputs, DagSourceRequest request)
        => request.LayerId;

    /// <summary>
    /// Rejects, BEFORE any layer read, an <c>outStatistics</c> request that is malformed or that
    /// names a field the aggregated layer does not have (#4624). Without this a misspelled field
    /// produced a well-formed artifact with a null aggregate column — an ignored statistic
    /// indistinguishable from "no values". Skipped when catalog metadata (or the layer's schema)
    /// is unavailable; the parse itself always runs.
    /// </summary>
    private async Task ValidateStatisticsAsync(
        IServiceProvider services,
        StepInputReader inputs,
        DagSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGet("outStatistics", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var stats = StatisticsSupport.ParseOutStatistics(raw);
        var metadata = services.GetService<IMetadataV2GraphProvider>();
        if (metadata is null || ResolveStatisticsLayerId(inputs, request) is not { } layerId)
        {
            return;
        }

        var snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource)
            || resource.SchemaFields.Count == 0)
        {
            return;
        }

        var primaryId = resource.FindPrimaryIdField()?.Name;
        foreach (var field in stats.Select(spec => spec.Field).Where(field => field.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(field, primaryId, StringComparison.Ordinal)
                || resource.SchemaFields.Any(schemaField => string.Equals(schemaField.Name, field, StringComparison.Ordinal)))
            {
                continue;
            }

            var caseMatch = resource.SchemaFields
                .FirstOrDefault(schemaField => string.Equals(schemaField.Name, field, StringComparison.OrdinalIgnoreCase));
            throw new TransformInputException(
                $"'outStatistics' references field '{field}', which is not a field of layer {layerId}"
                + (caseMatch is null ? "." : $"; did you mean '{caseMatch.Name}'?"));
        }
    }

    private static string? ResolveObjectIds(StepInputReader inputs)
    {
        if (!inputs.TryGet("objectIds", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (var token in raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new TransformInputException(
                    $"'objectIds' must be a comma-separated list of integer feature identifiers; " +
                    $"'{token}' is not valid.");
            }
        }

        return raw;
    }

    private static IDagFeatureSource? ResolveHonuaLayerSource(IServiceProvider services) =>
        services.GetServices<IDagFeatureSource>()
            .FirstOrDefault(candidate => string.Equals(candidate.SourceId, HonuaLayerSourceId, StringComparison.Ordinal));

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
