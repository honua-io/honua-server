// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class EnrichmentJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "enrichment.enrich";

    internal const string NearDistanceAttribute = "NEAR_DIST";

    // The backing layer id is stamped at submission after any caller value is stripped, and a dataset that resolves to a different layer is refused.
    internal const string AuthorizedDatasetLayerInput = "authorizedDatasetLayerId";

    // The source layer is pinned at submission so a later run under the orchestrator admin role cannot authorize a different layer.
    internal const string AuthorizedSourceLayerInput = "authorizedSourceLayerId";

    // Enrichment compute is a curated facade over spatial join, so it shares the
    // spatial-join entitlement rather than introducing a separate SKU line
    // (mirrors DataEnrichmentRequestHandlers).
    private const string EnrichmentEntitlementKey = "analytics.spatial-join";

    private const string HonuaLayerSourceId = "source.honua-layer";

    // Both layers are pinned to EPSG:4326 because GeoJSON is WGS 84, so distances are in degrees with no geodesic conversion.
    private const int JoinSrid = 4326;

    private const int DefaultMaxInputFeatures = 250_000;

    // A caller may lower the admission cap and cannot raise it past this ceiling.
    private const int MaxInputFeaturesCeiling = 1_000_000;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<EnrichmentJobExecutor> _logger;
    private readonly int _maxVerticesPerGeometry;
    private readonly long _maxGeometryBytes;
    private readonly long _maxInputBytes;
    private IReadOnlySet<string>? _processIds;

    public EnrichmentJobExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<EnrichmentJobExecutor> logger,
        IOptions<LimitsOptions>? limitsOptions = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
        // Same per-geometry vertex ceiling the layer-sourced executors charge (#4629): a
        // feature-count cap alone does not bound a single deliberately oversized geometry.
        var limits = limitsOptions?.Value ?? new LimitsOptions();
        _maxVerticesPerGeometry = limits.Geometry.MaxVerticesPerGeometry;
        // ... and the same serialized-size budgets, per geometry and per layer read.
        _maxGeometryBytes = limits.Geometry.MaxGeometrySize;
        _maxInputBytes = limits.Analytics.MaxInputBytes;
    }

    public IReadOnlySet<string> ProcessIds =>
        _processIds ??= new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seconds = _options.CurrentValue.MaxLayerExecutionSeconds;
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await ExecuteCoreAsync(job, context, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return LayerComputationBudget.Refusal(
                $"{HandledProcessId} exceeded Geoprocessing:Executors:MaxLayerExecutionSeconds={seconds}; " +
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
            return LayerComputationBudget.Refusal(
                $"Invalid {HandledProcessId} inputs: supply exactly one source — 'layerId' (registered source layer) "
                + "or 'input' (staged FeatureCollection data URI).");
        }

        // Layer-only windowing filters (honua-server#3043 review). 'where' and 'bbox' are
        // translated into the source.honua-layer read; the inline branch below parses the
        // staged FeatureCollection verbatim and applies NEITHER. Accepting them alongside
        // 'input' silently enriched every staged feature, so the job "succeeded" with a
        // result broader than the caller asked for. The catalog documents both parameters
        // as layer-source-only, so the combination is refused rather than quietly ignored.
        if (hasInline && FindLayerOnlySourceFilter(inputs) is { } layerOnlyFilter)
        {
            return LayerComputationBudget.Refusal(
                $"Invalid {HandledProcessId} inputs: '{layerOnlyFilter}' windows the registered source layer read "
                + "and is only valid with 'layerId'; a staged 'input' FeatureCollection is enriched verbatim. "
                + $"Remove '{layerOnlyFilter}', or filter the staged collection before submitting it.");
        }

        if (!inputs.TryGetRequired("datasetId", out var datasetId, out var missingDataset))
        {
            return LayerComputationBudget.Refusal($"Invalid {HandledProcessId} inputs: {missingDataset}.");
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
            return LayerComputationBudget.Refusal(
                $"Unknown enrichment dataset '{datasetId}': no managed or configured dataset matches this id.");
        }

        // Bound-layer verification (honua-server#3043 review). The submit-time gate
        // authorized the dataset's backing layer as it stood at submission and stamped that
        // identity onto the step; a managed dataset can be re-pointed at a DIFFERENT layer
        // while this job sits in the queue, and reading whatever is current would read a
        // layer nobody authorized. Both a mismatch and a missing binding fail the job before
        // any layer read — an unstamped job never cleared the gate.
        if (CheckAuthorizedDatasetLayer(inputs, dataset, job.OperationId) is { } bindingError)
        {
            return JobExecutionResult.Failed(bindingError);
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
            return LayerComputationBudget.Refusal($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
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
                : await ReadSourceLayerAsync(
                        source, inputs, plan, _maxVerticesPerGeometry, _maxGeometryBytes, _maxInputBytes, _options.CurrentValue.MaxLayerVertices, cancellationToken)
                    .ConfigureAwait(false);

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
                    $"enrichment dataset layer {dataset.LayerId}",
                    _maxVerticesPerGeometry,
                    _maxGeometryBytes,
                    _maxInputBytes,
                    _options.CurrentValue.MaxLayerVertices)
                .ConfigureAwait(false);
        }
        catch (TransformInputException ex)
        {
            return LayerComputationBudget.Refusal($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
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
            LayerComputationBudget.EnsureTopologyWork(
                LayerComputationBudget.CountVertices(targets),
                LayerComputationBudget.CountVertices(joinFeatures),
                _options.CurrentValue.MaxTopologyWork);
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
            return LayerComputationBudget.Refusal($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ComputationFailed(_logger, job.OperationId, dataset.Id, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} computation failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(85, "Encoding enrichment artifact", cancellationToken).ConfigureAwait(false);

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        byte[] payload;
        try
        {
            payload = BoundedArtifactWriter.WriteFeatureCollection(
                output, HandledProcessId, maxBytes, cancellationToken, BuildProvenanceMembers(dataset, plan));
        }
        catch (TransformInputException ex)
        {
            return LayerComputationBudget.Refusal($"{HandledProcessId} {ex.PublicMessage}");
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

    private string? CheckAuthorizedDatasetLayer(
        StepInputReader inputs,
        EnrichmentDatasetDefinition dataset,
        string operationId)
    {
        if (!inputs.TryGet(AuthorizedDatasetLayerInput, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authorizedLayerId))
        {
            Log.DatasetLayerBindingMissing(_logger, operationId, dataset.Id);
            return $"{HandledProcessId} cannot verify which enrichment dataset layer this job was "
                + "authorized to read: the job carries no authorized dataset-layer binding. Resubmit the job.";
        }

        if (authorizedLayerId == dataset.LayerId)
        {
            return null;
        }

        Log.DatasetLayerRebound(_logger, operationId, dataset.Id, authorizedLayerId, dataset.LayerId);
        return $"Enrichment dataset '{dataset.Id}' was re-pointed after this job was submitted: it now "
            + $"resolves to layer {dataset.LayerId}, but the job was authorized to read layer "
            + $"{authorizedLayerId}. Resubmit so the current layer is authorized.";
    }

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

    private static string? FindLayerOnlySourceFilter(StepInputReader inputs)
    {
        if (inputs.TryGet("where", out _))
        {
            return "where";
        }

        return inputs.TryGet("bbox", out _) ? "bbox" : null;
    }

    private static IDagFeatureSource? ResolveHonuaLayerSource(IServiceProvider services) =>
        services.GetServices<IDagFeatureSource>()
            .FirstOrDefault(candidate => string.Equals(candidate.SourceId, HonuaLayerSourceId, StringComparison.Ordinal));

    private List<IFeature> ParseInlineSource(string inlineUri, int maxFeatures)
    {
        if (!FeatureCollectionArtifact.TryParseDataUri(
                inlineUri, out var collection, out var error, Math.Min(_options.CurrentValue.MaxArtifactBytes, _maxInputBytes)))
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

        var totalVertices = LayerComputationBudget.CountVertices(collection);
        if (totalVertices > _options.CurrentValue.MaxLayerVertices
            || collection.Any(feature => (feature.Geometry?.NumPoints ?? 0) > _maxVerticesPerGeometry))
        {
            throw new TransformInputException(
                "staged 'input' exceeds the configured MaxLayerVertices or MaxVerticesPerGeometry budget; " +
                "simplify the input or stage fewer features, then resubmit.");
        }

        return [.. collection];
    }

    private static Task<List<IFeature>> ReadSourceLayerAsync(
        IDagFeatureSource source,
        StepInputReader inputs,
        EnrichmentPlan plan,
        int maxVerticesPerGeometry,
        long maxGeometryBytes,
        long maxInputBytes,
        int maxTotalVertices,
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
            source, request, cancellationToken, plan.MaxInputFeatures, $"source layer {layerId}", maxVerticesPerGeometry,
            maxGeometryBytes, maxInputBytes, maxTotalVertices);
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
        SpatialJoinPredicate predicate;
        switch (methodName)
        {
            case "":
                predicate = ParsePredicate(
                    inputs.GetOrDefault("predicate", dataset.DefaultPredicate));
                methodName = WireMethodName(predicate);
                break;
            case "intersects":
                predicate = SpatialJoinPredicate.Intersects;
                break;
            case "point-in-polygon" or "point_in_polygon" or "pip" or "contains":
                predicate = SpatialJoinPredicate.JoinContainsTarget;
                break;
            case "within":
                predicate = SpatialJoinPredicate.TargetContainsJoin;
                break;
            case "within-distance" or "within_distance" or "dwithin":
                predicate = SpatialJoinPredicate.DWithin;
                break;
            case "nearest-neighbor" or "nearest_neighbor" or "nearest":
                nearest = true;
                predicate = SpatialJoinPredicate.Intersects;
                break;
            default:
                throw new TransformInputException(
                    $"method '{methodName}' is not supported (allowed: intersects, point-in-polygon, within, "
                    + "within-distance, nearest-neighbor)");
        }

        // Runtime counterpart of the validator's submit-time refusal. The nearest branch of
        // Enrich returns after AnnotateNearest without consulting plan.Stats, so accepting the
        // combination here would produce an artifact silently missing every requested
        // statistic. Aggregates summarise a match set; nearest-neighbor yields one closest
        // feature per target and has no set to summarise (#3043 review).
        if (nearest && stats.Count > 0)
        {
            throw new TransformInputException(
                "'aggregates' is supported on the join methods only (intersects, point-in-polygon, within, "
                + "within-distance); the nearest-neighbor method annotates each target with its single closest "
                + "dataset feature (NEAR_DIST) and has no match set to aggregate");
        }

        var distance = 0d;
        // The within-distance threshold is evaluated in the CRS units of the layer
        // geometries (no geodesic conversion on this managed path), so the sync
        // endpoint's meters-based dataset default is deliberately NOT inherited —
        // callers must state the threshold explicitly.
        if (!nearest
            && predicate == SpatialJoinPredicate.DWithin
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
            TryReadNonNegativeLong(inputs, "maxCarriedMatchValues") ?? SpatialJoinSupport.DefaultMaxCarriedMatchValues,
            SpatialJoinSupport.DefaultMaxCarriedMatchValues);

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

    // The canonical members name both operands (honua-server#3069), so the effective
    // method echoed as artifact provenance is mapped back to THIS surface's wire
    // vocabulary rather than derived from the enum member name.
    private static string WireMethodName(SpatialJoinPredicate predicate)
        => predicate switch
        {
            SpatialJoinPredicate.JoinContainsTarget => "contains",
            SpatialJoinPredicate.TargetContainsJoin => "within",
            SpatialJoinPredicate.DWithin => "dwithin",
            _ => "intersects",
        };

    // Enrichment's wire vocabulary is DATASET-SUBJECT: the dataset layer supplies the
    // join geometries, so `contains`/`point-in-polygon` means the dataset polygon
    // contains the caller's source feature. Unchanged behavior — the canonical member
    // names simply make the operand order explicit.
    private static SpatialJoinPredicate ParsePredicate(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "" or "intersects" => SpatialJoinPredicate.Intersects,
            "contains" => SpatialJoinPredicate.JoinContainsTarget,
            "within" => SpatialJoinPredicate.TargetContainsJoin,
            "dwithin" => SpatialJoinPredicate.DWithin,
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
            // Nearest-neighbor carries at most one value per requested field per target,
            // but those values still belong to the advertised cumulative ceiling.
            var nearestBudget = new SpatialJoinSupport.MatchBudget(plan.MaxCarriedMatchValues);
            // Index the dataset ONCE and query it per target, so nearest-neighbor is
            // O(targets · log dataset) rather than a full O(targets × dataset) scan that
            // would effectively hang on an ordinary large reference dataset.
            var nearestIndex = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Add(AnnotateNearest(target, nearestIndex, plan.CarryFields, nearestBudget));
            }

            return output;
        }

        var index = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);
        // One budget for the WHOLE join: the per-layer caps bound each input, but the
        // match set is a Cartesian product, so overlapping layers can buffer far more
        // carried values — and compare far more candidate pairs — than either input has
        // features.
        var budget = new SpatialJoinSupport.MatchBudget(plan.MaxCarriedMatchValues);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(SpatialJoinSupport.Join(
                target, index, plan.Predicate, plan.Distance, plan.CarryFields, plan.Stats, budget,
                cancellationToken));
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
        IReadOnlyList<string> carryFields,
        SpatialJoinSupport.MatchBudget budget)
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

        if (nearestFeature is not null)
        {
            budget.Charge(carryFields.Count);
        }

        // Carried attributes FIRST, computed fields after — same collision rule as the join
        // path: a dataset carrying JOIN_COUNT or NEAR_DIST must not overwrite the values this
        // method promises (honua-server#3043 review).
        foreach (var field in carryFields)
        {
            OverlayExecutorSupport.Upsert(
                attributes,
                field,
                nearestFeature is null ? null : SpatialJoinSupport.ReadValue(nearestFeature, field));
        }

        OverlayExecutorSupport.Upsert(attributes, SpatialJoinSupport.JoinCountAttribute, nearestFeature is null ? 0L : 1L);
        OverlayExecutorSupport.Upsert(
            attributes, NearDistanceAttribute, nearestFeature is null ? null : (object)bestDistance);

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

    private sealed record EnrichmentPlan(
        string MethodName,
        bool Nearest,
        SpatialJoinPredicate Predicate,
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

        [LoggerMessage(9326, LogLevel.Warning,
            "enrichment.enrich refused job {OperationId} for dataset {DatasetId}: no authorized dataset-layer binding is present")]
        public static partial void DatasetLayerBindingMissing(ILogger logger, string operationId, string datasetId);

        [LoggerMessage(9327, LogLevel.Warning,
            "enrichment.enrich refused job {OperationId}: dataset {DatasetId} was re-pointed from authorized layer {AuthorizedLayerId} to layer {CurrentLayerId} after submission")]
        public static partial void DatasetLayerRebound(
            ILogger logger, string operationId, string datasetId, int authorizedLayerId, int currentLayerId);

        [LoggerMessage(9325, LogLevel.Information,
            "enrichment.enrich job {OperationId} completed for dataset {DatasetId} method {Method}: {InputCount} inputs, {ResultCount} results")]
        public static partial void EnrichmentCompleted(ILogger logger, string operationId, string datasetId, string method, int inputCount, int resultCount);
    }
}
