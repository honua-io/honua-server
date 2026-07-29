// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>enrichment.enrich</c> executor (#2283): the asynchronous batch counterpart of
/// the synchronous <c>POST /api/enrich</c> endpoint, running as a canonical
/// geoprocessing job so submission, status, results, and dismissal all flow through
/// the existing process/job runtime (OGC API Processes <c>/ogc/processes/jobs</c>,
/// GPServer) instead of an enrichment-local lifecycle.
///
/// <para>
/// The enrichment dataset is resolved by <c>datasetId</c> through the neutral
/// <see cref="IEnrichmentDatasetResolver"/> seam — the same merged
/// managed/configuration catalog the sync endpoint uses — and the dataset's
/// minimum-edition tier plus the shared <c>analytics.spatial-join</c> entitlement
/// are enforced before any layer read. The target features come from EITHER a
/// registered catalog layer (<c>layerId</c>, streamed through the
/// <c>source.honua-layer</c> connector with optional <c>where</c>/<c>bbox</c>
/// windowing) OR a staged inline FeatureCollection (<c>input</c>, a
/// <c>data:application/geo+json;base64</c> data URI) — closing the sync endpoint's
/// inline-source and over-limit deferrals. The dataset's backing layer is always
/// streamed through the connector.
/// </para>
///
/// <para>
/// The join computation is the shared <see cref="SpatialJoinSupport"/> used by
/// <c>analytics.spatial-join</c> (JOIN_COUNT + carried-attribute arrays + numeric
/// aggregates; managed NTS predicates, CRS-unit distances, no geodesic
/// conversion), plus a <c>nearest-neighbor</c> method that annotates each target
/// with the single closest dataset feature's carried attributes and a
/// <c>NEAR_DIST</c> planar distance. The published artifact is the canonical
/// FeatureCollection envelope with the dataset id, title, and attribution injected
/// as foreign members so downstream consumers can comply with the data provider's
/// terms.
/// </para>
/// </summary>
internal sealed partial class EnrichmentJobExecutor : IProcessExecutor
{
    /// <summary>The dotted process id this executor handles.</summary>
    internal const string HandledProcessId = "enrichment.enrich";

    /// <summary>Attribute holding the planar distance to the nearest dataset feature.</summary>
    internal const string NearDistanceAttribute = "NEAR_DIST";

    // Enrichment compute is a curated facade over spatial join, so it shares the
    // spatial-join entitlement rather than introducing a separate SKU line
    // (mirrors DataEnrichmentRequestHandlers).
    private const string EnrichmentEntitlementKey = "analytics.spatial-join";

    private const string HonuaLayerSourceId = "source.honua-layer";

    /// <summary>
    /// The single CRS both layers are streamed in and the artifact is published in:
    /// EPSG:4326. Pinning one CRS is what makes a cross-SRID join correct, and GeoJSON
    /// is WGS 84 by specification (RFC 7946), so publishing projected ordinates would
    /// be misread by every standard consumer. Distances are therefore evaluated in
    /// degrees — the same "CRS units, no geodesic conversion" contract the other managed
    /// analytics executors document.
    /// </summary>
    private const int JoinSrid = 4326;

    /// <summary>
    /// Default per-layer admission cap. Enforced while streaming so an oversized
    /// selection fails fast with an actionable message instead of exhausting worker
    /// memory before the artifact-size check.
    /// </summary>
    private const int DefaultMaxInputFeatures = 250_000;

    /// <summary>
    /// Hard operator ceiling for the per-layer admission cap. A caller may only lower
    /// the cap; it can never be raised past this, so no single job can disable the
    /// guard and exhaust the worker.
    /// </summary>
    private const int MaxInputFeaturesCeiling = 1_000_000;

    /// <summary>
    /// Cumulative ceiling on carried match values across an entire join. Bounds the
    /// Cartesian growth the per-layer input caps cannot see (targets x matches x
    /// carried fields), so two individually permitted but highly overlapping layers
    /// cannot exhaust the worker before the artifact-size check.
    /// </summary>
    private const long DefaultMaxCarriedMatchValues = 20_000_000L;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<EnrichmentJobExecutor> _logger;
    private IReadOnlySet<string>? _processIds;

    /// <summary>Initializes a new instance of the <see cref="EnrichmentJobExecutor"/> class.</summary>
    public EnrichmentJobExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<EnrichmentJobExecutor> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds =>
        _processIds ??= new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

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
        if (!string.Equals(resolved, HandledProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        var inputs = new StepInputReader(job.Spec.Parameters);
        var hasLayerId = inputs.TryGet("layerId", out _);
        var hasInline = inputs.TryGet("input", out var inlineUri);
        if (hasLayerId == hasInline)
        {
            return JobExecutionResult.Failed(
                $"Invalid {HandledProcessId} inputs: supply exactly one source — 'layerId' (registered source layer) "
                + "or 'input' (staged FeatureCollection data URI).");
        }

        if (!inputs.TryGetRequired("datasetId", out var datasetId, out var missingDataset))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {missingDataset}.");
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var resolver = services.GetService<IEnrichmentDatasetResolver>();
        if (resolver is null)
        {
            Log.ResolverUnavailable(_logger, job.OperationId);
            return JobExecutionResult.Failed(
                $"The {HandledProcessId} process is unavailable in this deployment: no enrichment dataset catalog "
                + "is registered.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Resolving enrichment dataset", cancellationToken).ConfigureAwait(false);

        var dataset = await resolver.ResolveAsync(datasetId, cancellationToken).ConfigureAwait(false);
        if (dataset is null)
        {
            return JobExecutionResult.Failed(
                $"Unknown enrichment dataset '{datasetId}': no managed or configured dataset matches this id.");
        }

        if (CheckLicenseGate(services, dataset) is { } licenseError)
        {
            Log.LicenseDenied(_logger, job.OperationId, dataset.Id);
            return JobExecutionResult.Failed(licenseError);
        }

        EnrichmentPlan plan;
        try
        {
            plan = BuildPlan(inputs, dataset);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
        }

        var source = ResolveHonuaLayerSource(services);
        if (source is null)
        {
            Log.SourceUnavailable(_logger, job.OperationId);
            return JobExecutionResult.Failed(
                $"The {HandledProcessId} process is unavailable in this deployment: it reads the enrichment "
                + $"dataset's layer through the {HonuaLayerSourceId} connector, which is not configured here.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(20, "Reading source features", cancellationToken).ConfigureAwait(false);

        List<IFeature> targets;
        List<IFeature> joinFeatures;
        try
        {
            targets = hasInline
                ? ParseInlineSource(inlineUri!, plan.MaxInputFeatures)
                : await ReadSourceLayerAsync(source, inputs, plan, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(45, "Reading enrichment dataset layer", cancellationToken).ConfigureAwait(false);

            // Both layers are streamed in the SAME CRS (JoinSrid) so the managed
            // NTS predicates and distances compare comparable ordinates; without this a
            // 4326 source joined to a 3857 dataset would silently mismatch.
            joinFeatures = await LayerSourcedFeatureExecutor.ReadLayerAsync(
                    source,
                    new DagSourceRequest { LayerId = dataset.LayerId, OutputSrid = JoinSrid },
                    cancellationToken,
                    plan.MaxInputFeatures,
                    $"enrichment dataset layer {dataset.LayerId}")
                .ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
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
            Log.SourceReadFailed(_logger, job.OperationId, dataset.Id, ex);
            return JobExecutionResult.Failed(
                $"{HandledProcessId} failed reading the source or dataset layer: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(65, "Enriching features", cancellationToken).ConfigureAwait(false);

        List<IFeature> output;
        try
        {
            output = Enrich(targets, joinFeatures, plan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TransformInputException ex)
        {
            // The cumulative match budget surfaces here with concrete remedies (narrow
            // where/bbox, carry fewer outputFields, use a less permissive method), so it
            // must reach the caller verbatim rather than collapsing to a type name.
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ComputationFailed(_logger, job.OperationId, dataset.Id, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} computation failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(85, "Encoding enrichment artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(
            output, HandledProcessId, BuildProvenanceMembers(dataset, plan));
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{HandledProcessId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        await context.PublishArtifactAsync(FeatureCollectionArtifact.BuildDataUri(payload), cancellationToken)
            .ConfigureAwait(false);
        await context.ReportProgressAsync(
                100,
                $"{HandledProcessId} completed ({output.Count} features enriched from dataset '{dataset.Id}')",
                cancellationToken)
            .ConfigureAwait(false);

        Log.EnrichmentCompleted(_logger, job.OperationId, dataset.Id, plan.MethodName, targets.Count, output.Count);
        return JobExecutionResult.Succeeded();
    }

    /// <summary>
    /// Enforces the shared enrichment entitlement and the dataset's minimum-edition
    /// tier from the deployment's license state, mirroring the synchronous
    /// endpoint's <c>LicenseGate</c> + minimum-edition checks. Returns a failure
    /// message, or null when the caller is entitled.
    /// </summary>
    private static string? CheckLicenseGate(IServiceProvider services, EnrichmentDatasetDefinition dataset)
    {
        var entitlementService = services.GetService<ILicenseEntitlementService>();
        var statusProvider = services.GetService<ILicenseStatusProvider>();
        var edition = statusProvider?.GetCurrentStatus().Edition ?? HonuaEdition.Community;

        // Enrichment compute reuses the analytics.spatial-join entitlement (Pro tier).
        var entitled = entitlementService is not null
            ? entitlementService.CheckEntitlement(EnrichmentEntitlementKey).IsActive
            : edition >= HonuaEdition.Pro;
        if (!entitled)
        {
            return $"Data enrichment requires an active '{EnrichmentEntitlementKey}' entitlement "
                + $"(Pro edition); the current edition is {edition}.";
        }

        if (edition < dataset.MinimumEdition)
        {
            return $"Enrichment dataset '{dataset.Id}' requires the {dataset.MinimumEdition} edition; "
                + $"the current edition is {edition}.";
        }

        return null;
    }

    private static IDagFeatureSource? ResolveHonuaLayerSource(IServiceProvider services) =>
        services.GetServices<IDagFeatureSource>()
            .FirstOrDefault(candidate => string.Equals(candidate.SourceId, HonuaLayerSourceId, StringComparison.Ordinal));

    private List<IFeature> ParseInlineSource(string inlineUri, int maxFeatures)
    {
        if (!FeatureCollectionArtifact.TryParseDataUri(
                inlineUri, out var collection, out var error, _options.CurrentValue.MaxArtifactBytes))
        {
            throw new TransformInputException($"'input' {error}");
        }

        // The admission cap is a property of the REQUEST, not of the source form: an
        // inline collection must trip the same guard a layer-backed selection would,
        // before the dataset layer is read and the join is computed.
        if (collection.Count > maxFeatures)
        {
            throw new TransformInputException(
                $"staged 'input' source exceeds the configured limit of {maxFeatures} features; "
                + "stage fewer features or raise the limit.");
        }

        return [.. collection];
    }

    private static Task<List<IFeature>> ReadSourceLayerAsync(
        IDagFeatureSource source,
        StepInputReader inputs,
        EnrichmentPlan plan,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGet("layerId", out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId)
            || layerId < 0)
        {
            throw new TransformInputException(
                "missing or invalid required input 'layerId'; expected a non-negative integer.");
        }

        var request = new DagSourceRequest
        {
            LayerId = layerId,
            Where = inputs.TryGet("where", out var where) ? where : null,
            Bbox = inputs.TryGet("bbox", out var bbox) ? bbox : null,
            // Normalized to the join CRS so the source and dataset layers are joined in
            // one coordinate system (see the dataset read).
            OutputSrid = JoinSrid,
        };

        return LayerSourcedFeatureExecutor.ReadLayerAsync(
            source, request, cancellationToken, plan.MaxInputFeatures, $"source layer {layerId}");
    }

    // Resolves the effective join behavior from the enrichment vocabulary: the
    // 'method' names (mirroring POST /api/enrich) take precedence over a raw
    // 'predicate', which falls back to the dataset default.
    private static EnrichmentPlan BuildPlan(StepInputReader inputs, EnrichmentDatasetDefinition dataset)
    {
        var carryFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("outputFields", string.Empty));
        if (carryFields.Count == 0)
        {
            carryFields = [.. dataset.Attributes];
        }

        var stats = StatisticsSupport.ParseStatistics(inputs.GetOrDefault("aggregates", string.Empty));

        var methodName = inputs.GetOrDefault("method", string.Empty).Trim().ToLowerInvariant();
        var nearest = false;
        SpatialJoinSupport.SpatialPredicate predicate;
        switch (methodName)
        {
            case "":
                predicate = ParsePredicate(
                    inputs.GetOrDefault("predicate", dataset.DefaultPredicate));
                methodName = predicate.ToString().ToLowerInvariant();
                break;
            case "intersects":
                predicate = SpatialJoinSupport.SpatialPredicate.Intersects;
                break;
            case "point-in-polygon" or "point_in_polygon" or "pip" or "contains":
                predicate = SpatialJoinSupport.SpatialPredicate.Contains;
                break;
            case "within":
                predicate = SpatialJoinSupport.SpatialPredicate.Within;
                break;
            case "within-distance" or "within_distance" or "dwithin":
                predicate = SpatialJoinSupport.SpatialPredicate.Dwithin;
                break;
            case "nearest-neighbor" or "nearest_neighbor" or "nearest":
                nearest = true;
                predicate = SpatialJoinSupport.SpatialPredicate.Intersects;
                break;
            default:
                throw new TransformInputException(
                    $"method '{methodName}' is not supported (allowed: intersects, point-in-polygon, within, "
                    + "within-distance, nearest-neighbor)");
        }

        var distance = 0d;
        // The within-distance threshold is evaluated in the CRS units of the layer
        // geometries (no geodesic conversion on this managed path), so the sync
        // endpoint's meters-based dataset default is deliberately NOT inherited —
        // callers must state the threshold explicitly.
        if (!nearest
            && predicate == SpatialJoinSupport.SpatialPredicate.Dwithin
            && (!inputs.TryGet("distance", out var distanceRaw)
                || !double.TryParse(distanceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
                || !double.IsFinite(distance)
                || distance <= 0))
        {
            throw new TransformInputException(
                "the within-distance method requires a finite positive 'distance' threshold in CRS units");
        }


        // The caller may only LOWER the admission cap. An operator ceiling still applies,
        // so a permitted caller cannot disable the guard (e.g. int.MaxValue) and exhaust
        // the worker before the artifact-size check.
        var maxInputFeatures = Math.Min(
            TryReadPositiveInt(inputs, "maxInputFeatures") ?? DefaultMaxInputFeatures,
            MaxInputFeaturesCeiling);

        // Same posture as the input cap: the caller may only LOWER the join budget.
        var maxCarriedMatchValues = Math.Min(
            TryReadNonNegativeLong(inputs, "maxCarriedMatchValues") ?? DefaultMaxCarriedMatchValues,
            DefaultMaxCarriedMatchValues);

        return new EnrichmentPlan(
            methodName, nearest, predicate, distance, carryFields, stats, maxInputFeatures, maxCarriedMatchValues);
    }

    private static long? TryReadNonNegativeLong(StepInputReader inputs, string name)
    {
        if (!inputs.TryGet(name, out var raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new TransformInputException($"'{name}' must be a non-negative integer.");
        }

        return value;
    }

    private static int? TryReadPositiveInt(StepInputReader inputs, string name)
    {
        if (!inputs.TryGet(name, out var raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new TransformInputException($"'{name}' must be a positive integer.");
        }

        return value;
    }

    private static SpatialJoinSupport.SpatialPredicate ParsePredicate(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "" or "intersects" => SpatialJoinSupport.SpatialPredicate.Intersects,
            "contains" => SpatialJoinSupport.SpatialPredicate.Contains,
            "within" => SpatialJoinSupport.SpatialPredicate.Within,
            "dwithin" => SpatialJoinSupport.SpatialPredicate.Dwithin,
            var other => throw new TransformInputException(
                $"predicate '{other}' is not supported (allowed: intersects, contains, within, dwithin)"),
        };

    private static List<IFeature> Enrich(
        List<IFeature> targets,
        List<IFeature> joinFeatures,
        EnrichmentPlan plan,
        CancellationToken cancellationToken)
    {
        var output = new List<IFeature>(targets.Count);
        if (plan.Nearest)
        {
            // Index the dataset ONCE and query it per target, so nearest-neighbor is
            // O(targets · log dataset) rather than a full O(targets × dataset) scan that
            // would effectively hang on an ordinary large reference dataset.
            var nearestIndex = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Add(AnnotateNearest(target, nearestIndex, plan.CarryFields));
            }

            return output;
        }

        var index = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);
        // One budget for the WHOLE join: the per-layer caps bound each input, but the
        // match set is a Cartesian product, so overlapping layers can buffer far more
        // carried values than either input has features.
        var budget = new SpatialJoinSupport.MatchBudget(plan.MaxCarriedMatchValues);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(SpatialJoinSupport.Join(
                target, index, plan.Predicate, plan.Distance, plan.CarryFields, plan.Stats, budget));
        }

        return output;
    }

    // nearest-neighbor: annotate the target with the single closest dataset
    // feature's carried attributes plus NEAR_DIST (planar CRS-unit distance,
    // matching the proximity tool pack). JOIN_COUNT is 1 when a neighbour exists
    // and 0 otherwise so the output stays shape-compatible with the join methods.
    private static Feature AnnotateNearest(
        IFeature target,
        STRtree<IFeature> nearestIndex,
        IReadOnlyList<string> carryFields)
    {
        var attributes = OverlayExecutorSupport.CopyAttributes(target);

        IFeature? nearestFeature = null;
        var bestDistance = double.PositiveInfinity;
        var geometry = target.Geometry;
        if (geometry is not null && !geometry.IsEmpty && nearestIndex.Count > 0)
        {
            nearestFeature = nearestIndex.NearestNeighbour(
                geometry.EnvelopeInternal, target, SpatialJoinSupport.FeatureDistance.Instance);
            var nearestGeometry = nearestFeature?.Geometry;
            if (nearestGeometry is null || nearestGeometry.IsEmpty)
            {
                nearestFeature = null;
            }
            else
            {
                bestDistance = geometry.Distance(nearestGeometry);
            }
        }

        OverlayExecutorSupport.Upsert(attributes, SpatialJoinSupport.JoinCountAttribute, nearestFeature is null ? 0L : 1L);
        OverlayExecutorSupport.Upsert(
            attributes, NearDistanceAttribute, nearestFeature is null ? null : (object)bestDistance);
        foreach (var field in carryFields)
        {
            OverlayExecutorSupport.Upsert(
                attributes,
                field,
                nearestFeature is null ? null : SpatialJoinSupport.ReadValue(nearestFeature, field));
        }

        return new Feature(target.Geometry, attributes);
    }

    private static List<(string Name, object Value)> BuildProvenanceMembers(
        EnrichmentDatasetDefinition dataset,
        EnrichmentPlan plan)
    {
        var members = new List<(string Name, object Value)>
        {
            ("datasetId", dataset.Id),
            ("method", plan.MethodName),
        };

        if (!string.IsNullOrWhiteSpace(dataset.Title))
        {
            members.Add(("datasetTitle", dataset.Title!));
        }

        if (!string.IsNullOrWhiteSpace(dataset.Attribution))
        {
            members.Add(("attribution", dataset.Attribution!));
        }

        return members;
    }

    /// <summary>The resolved enrichment join behavior for one job.</summary>
    /// <param name="MethodName">Effective enrichment method name, echoed as artifact provenance.</param>
    /// <param name="Nearest">Whether the nearest-neighbor method was requested.</param>
    /// <param name="Predicate">Effective spatial predicate for the join methods.</param>
    /// <param name="Distance">Within-distance threshold in CRS units (0 when unused).</param>
    /// <param name="CarryFields">Dataset attributes carried onto each enriched feature.</param>
    /// <param name="Stats">Aggregates computed over the matched dataset features.</param>
    /// <param name="MaxInputFeatures">
    /// Per-layer admission cap enforced WHILE streaming, so an oversized selection fails
    /// fast instead of exhausting worker memory before the artifact-size check.
    /// </param>
    /// <param name="MaxCarriedMatchValues">
    /// Cumulative ceiling on carried match values across the whole join, bounding the
    /// Cartesian growth the per-layer caps cannot see.
    /// </param>
    private sealed record EnrichmentPlan(
        string MethodName,
        bool Nearest,
        SpatialJoinSupport.SpatialPredicate Predicate,
        double Distance,
        IReadOnlyList<string> CarryFields,
        IReadOnlyList<StatisticsSupport.StatSpec> Stats,
        int MaxInputFeatures,
        long MaxCarriedMatchValues);

    private static partial class Log
    {
        [LoggerMessage(9320, LogLevel.Warning,
            "enrichment.enrich refused job {OperationId}: no IEnrichmentDatasetResolver is registered")]
        public static partial void ResolverUnavailable(ILogger logger, string operationId);

        [LoggerMessage(9321, LogLevel.Warning,
            "enrichment.enrich refused job {OperationId} for dataset {DatasetId}: license gate denied")]
        public static partial void LicenseDenied(ILogger logger, string operationId, string datasetId);

        [LoggerMessage(9322, LogLevel.Warning,
            "enrichment.enrich refused job {OperationId}: no source.honua-layer connector is registered")]
        public static partial void SourceUnavailable(ILogger logger, string operationId);

        [LoggerMessage(9323, LogLevel.Error,
            "enrichment.enrich failed job {OperationId} reading the source or dataset layer for dataset {DatasetId}")]
        public static partial void SourceReadFailed(ILogger logger, string operationId, string datasetId, Exception exception);

        [LoggerMessage(9324, LogLevel.Error,
            "enrichment.enrich failed job {OperationId} during computation for dataset {DatasetId}")]
        public static partial void ComputationFailed(ILogger logger, string operationId, string datasetId, Exception exception);

        [LoggerMessage(9325, LogLevel.Information,
            "enrichment.enrich job {OperationId} completed for dataset {DatasetId} method {Method}: {InputCount} inputs, {ResultCount} results")]
        public static partial void EnrichmentCompleted(ILogger logger, string operationId, string datasetId, string method, int inputCount, int resultCount);
    }
}
