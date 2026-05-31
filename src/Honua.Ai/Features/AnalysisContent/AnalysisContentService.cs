// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.NlQuery.Services;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using StackExchange.Redis;

namespace Honua.Ai.AnalysisContent;

internal sealed partial class AnalysisContentService(
    IAnalysisContentStore store,
    IMetadataV2GraphProvider metadataV2GraphProvider,
    IQueryProcessor queryProcessor,
    IFeatureReader featureReader,
    IGeoprocessingJobService geoprocessingJobService,
    IEnumerable<IExecutionLogStore> logStores,
    TimeProvider timeProvider,
    ILogger<AnalysisContentService> logger) : IAnalysisContentService
{
    private const int DefaultPreviewLimit = 25;
    private const int MaxPreviewLimit = 200;
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;

    // Backend-neutral compute-unit projection. A query step costs a base unit plus a unit per
    // estimated million input features it reads; a geoprocessing step costs a fixed compute unit;
    // any other step costs a small fixed overhead. These coefficients are deliberate, documented
    // constants so the estimate is deterministic and reproducible for a given plan + statistics.
    private const double ComputeUnitsPerStepOverhead = 0.25d;
    private const double ComputeUnitsPerQueryStep = 1.0d;
    private const double ComputeUnitsPerGeoprocessStep = 2.0d;
    private const double ComputeUnitsPerMillionInputFeatures = 1.5d;
    private const decimal CostUsdPerComputeUnit = 0.02m;
    private const double RuntimeSecondsPerComputeUnit = 4.0d;

    private static readonly string[] LayerInputKeys = ["layerId", "layer", "storageLayerId", "sourceLayerId"];
    private const int DefaultLogLimit = 100;
    private const int MaxLogLimit = 200;
    private const int MaxDiagnosticLength = 512;
    private const int MaxVersionCreateAttempts = 3;
    private static readonly TimeSpan PreviewRetention = TimeSpan.FromHours(1);

    private readonly IExecutionLogStore? _logStore = logStores.FirstOrDefault();

    public async Task<AnalysisContentItemResult> CreateItemAsync(
        CreateAnalysisContentItemCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.CreateItem,
            async activity =>
            {
                ValidateCreate(command);

                var now = timeProvider.GetUtcNow();
                var itemId = CreateItemId();
                var version = CreateVersion(
                    itemId,
                    1,
                    command.Kind,
                    command.SavedQuery,
                    command.AnalysisPackage,
                    basedOnVersionId: null,
                    createdFromJobId: null,
                    createdFromArtifactIds: [],
                    createdBy: ResolveActor(principal),
                    now);

                var actor = ResolveActor(principal);
                var item = new AnalysisContentItem
                {
                    ItemId = itemId,
                    Kind = command.Kind,
                    Name = NormalizeName(command.Name),
                    Title = NormalizeOptional(command.Title),
                    OwnerId = actor,
                    CurrentVersion = version.Version,
                    CurrentVersionId = version.VersionId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = actor
                };

                AnalysisContentTelemetry.SetContentTags(activity, item.ItemId, item.Kind, version.Version, version.VersionId);
                try
                {
                    var stored = await store.CreateItemAsync(item, version, cancellationToken).ConfigureAwait(false);
                    Log.ContentItemCreated(logger, stored.ItemId, stored.Kind.ToString(), stored.CurrentVersion);
                    return new AnalysisContentItemResult(stored, version);
                }
                catch (AnalysisContentStoreConflictException ex)
                {
                    throw new AnalysisContentConflictException(ex.Message);
                }
            }).ConfigureAwait(false);

    public async Task<AnalysisContentItemResult> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.GetItem,
            async activity =>
            {
                var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                var version = await GetRequiredVersionAsync(itemId, null, cancellationToken).ConfigureAwait(false);
                AnalysisContentTelemetry.SetContentTags(activity, item.ItemId, item.Kind, version.Version, version.VersionId);
                return new AnalysisContentItemResult(item, version);
            }).ConfigureAwait(false);

    public async Task<AnalysisContentListResult> ListItemsAsync(
        ListAnalysisContentItemsQuery query,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.ListItems,
            async activity =>
            {
                ArgumentNullException.ThrowIfNull(query);

                var limit = ResolveLimit(query.Limit, DefaultListLimit, MaxListLimit);
                var offset = query.Offset.GetValueOrDefault();
                if (offset < 0)
                {
                    throw new AnalysisContentValidationException("Offset must be zero or greater.");
                }

                var page = await store.ListItemsAsync(
                    new AnalysisContentItemQuery
                    {
                        Kind = query.Kind,
                        Lifecycle = query.Lifecycle,
                        Limit = limit,
                        Offset = offset
                    },
                    cancellationToken).ConfigureAwait(false);

                activity?.SetTag("honua.analysis_content.list_count", page.Items.Count);
                activity?.SetTag("honua.analysis_content.list_total", page.TotalCount);
                return new AnalysisContentListResult(page.Items, page.TotalCount, limit, offset);
            }).ConfigureAwait(false);

    public async Task<AnalysisContentEstimateResult> EstimateVersionAsync(
        string itemId,
        int version,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.EstimateVersion,
            async activity =>
            {
                var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                var package = contentVersion.AnalysisPackage
                    ?? throw new AnalysisContentValidationException("The requested version is not an analysis package.");
                AnalysisContentTelemetry.SetContentTags(
                    activity,
                    contentVersion.ItemId,
                    contentVersion.Kind,
                    contentVersion.Version,
                    contentVersion.VersionId);

                var estimate = await ComputePackageEstimateAsync(package, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("honua.analysis_content.estimated_compute_units", estimate.EstimatedComputeUnits);
                return new AnalysisContentEstimateResult(
                    contentVersion.ItemId,
                    contentVersion.Version,
                    contentVersion.VersionId,
                    estimate);
            }).ConfigureAwait(false);

    private async Task<AnalysisContentEstimate> ComputePackageEstimateAsync(
        AnalysisPackageContent package,
        CancellationToken cancellationToken)
    {
        var steps = package.Plan.Steps;
        var queryStepCount = 0;
        var geoprocessStepCount = 0;
        var inputs = new List<AnalysisContentLayerEstimate>();
        long? estimatedInputFeatures = null;
        var isLowerBound = false;

        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case AnalysisPlanStepKind.QueryFeatures:
                    queryStepCount++;
                    break;
                case AnalysisPlanStepKind.Geoprocess:
                    geoprocessStepCount++;
                    break;
            }

            if (!TryResolveLayerInput(step, out var layerId))
            {
                continue;
            }

            long? layerCount = null;
            var unresolved = false;
            try
            {
                var layerEstimate = await featureReader.GetEstimatesAsync(layerId, cancellationToken).ConfigureAwait(false);
                layerCount = layerEstimate.EstimatedCount;
                estimatedInputFeatures = (estimatedInputFeatures ?? 0) + layerEstimate.EstimatedCount;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing or unreadable layer must not fail the estimate; record it as an
                // unresolved input so the projection is reported as a lower bound instead.
                unresolved = true;
                isLowerBound = true;
                Log.EstimateLayerUnresolved(logger, layerId, ex);
            }

            inputs.Add(new AnalysisContentLayerEstimate
            {
                StepId = step.StepId,
                LayerId = layerId,
                EstimatedFeatureCount = layerCount,
                Unresolved = unresolved
            });
        }

        var featureUnits = estimatedInputFeatures.HasValue
            ? estimatedInputFeatures.Value / 1_000_000d * ComputeUnitsPerMillionInputFeatures
            : 0d;
        var computeUnits =
            steps.Count * ComputeUnitsPerStepOverhead
            + queryStepCount * ComputeUnitsPerQueryStep
            + geoprocessStepCount * ComputeUnitsPerGeoprocessStep
            + featureUnits;
        computeUnits = Math.Round(computeUnits, 4, MidpointRounding.AwayFromZero);

        var costUsd = Math.Round((decimal)computeUnits * CostUsdPerComputeUnit, 4, MidpointRounding.AwayFromZero);
        var runtimeSeconds = Math.Round(computeUnits * RuntimeSecondsPerComputeUnit, 2, MidpointRounding.AwayFromZero);

        return new AnalysisContentEstimate
        {
            StepCount = steps.Count,
            QueryStepCount = queryStepCount,
            GeoprocessStepCount = geoprocessStepCount,
            EstimatedInputFeatureCount = estimatedInputFeatures,
            EstimatedComputeUnits = computeUnits,
            EstimatedCostUsd = costUsd,
            EstimatedRuntimeSeconds = runtimeSeconds,
            IsLowerBound = isLowerBound,
            Inputs = inputs
        };
    }

    private static bool TryResolveLayerInput(AnalysisPlanStep step, out int layerId)
    {
        foreach (var key in LayerInputKeys)
        {
            if (step.Inputs.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId)
                && layerId >= 0)
            {
                return true;
            }
        }

        layerId = 0;
        return false;
    }

    public async Task<AnalysisContentVersionResult> GetVersionAsync(
        string itemId,
        int? version,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.GetVersion,
            async activity =>
            {
                var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                AnalysisContentTelemetry.SetContentTags(
                    activity,
                    item.ItemId,
                    item.Kind,
                    contentVersion.Version,
                    contentVersion.VersionId);
                return new AnalysisContentVersionResult(item, contentVersion);
            }).ConfigureAwait(false);

    public async Task<AnalysisContentVersionResult> AddVersionAsync(
        string itemId,
        CreateAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.AddVersion,
            async activity =>
            {
                var item = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                var actor = ResolveActor(principal);
                var explicitBasedOnVersionId = NormalizeOptional(command.BasedOnVersionId);
                var createdFromJobId = NormalizeOptional(command.CreatedFromJobId);

                for (var attempt = 1; attempt <= MaxVersionCreateAttempts; attempt++)
                {
                    var latest = await GetRequiredVersionAsync(itemId, null, cancellationToken).ConfigureAwait(false);
                    var nextVersion = latest.Version + 1;
                    var now = timeProvider.GetUtcNow();

                    var version = CreateVersion(
                        itemId,
                        nextVersion,
                        item.Kind,
                        command.SavedQuery,
                        command.AnalysisPackage,
                        explicitBasedOnVersionId ?? latest.VersionId,
                        createdFromJobId,
                        command.CreatedFromArtifactIds ?? [],
                        actor,
                        now);

                    ValidatePayload(item.Kind, version.SavedQuery, version.AnalysisPackage);
                    AnalysisContentTelemetry.SetContentTags(
                        activity,
                        item.ItemId,
                        item.Kind,
                        version.Version,
                        version.VersionId);

                    try
                    {
                        var stored = await store.AddVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                        var updatedItem = await GetRequiredItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                        Log.ContentVersionCreated(logger, itemId, stored.Version);
                        return new AnalysisContentVersionResult(updatedItem, stored);
                    }
                    catch (AnalysisContentStoreConflictException) when (attempt < MaxVersionCreateAttempts)
                    {
                        Log.ContentVersionConflictRetry(logger, itemId, attempt);
                    }
                    catch (AnalysisContentStoreConflictException ex)
                    {
                        throw new AnalysisContentConflictException(
                            string.IsNullOrWhiteSpace(ex.Message)
                                ? "Analysis content version changed while creating a new version; retry the request."
                                : ex.Message);
                    }
                }

                throw new AnalysisContentConflictException(
                    "Analysis content version changed while creating a new version; retry the request.");
            }).ConfigureAwait(false);

    public async Task<SavedQueryPreviewResult> PreviewSavedQueryAsync(
        string itemId,
        int version,
        int? limit,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.PreviewSavedQuery,
            async activity =>
            {
                var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                var savedQuery = contentVersion.SavedQuery
                    ?? throw new AnalysisContentValidationException("The requested version is not a saved query.");
                AnalysisContentTelemetry.SetContentTags(
                    activity,
                    contentVersion.ItemId,
                    contentVersion.Kind,
                    contentVersion.Version,
                    contentVersion.VersionId);

                var snapshot = await metadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(savedQuery.LayerId, out var resource))
                {
                    throw new AnalysisContentNotFoundException($"Layer '{savedQuery.LayerId}' was not found.");
                }

                var previewLimit = ResolveLimit(limit ?? savedQuery.PreviewLimit, DefaultPreviewLimit, MaxPreviewLimit);
                QueryFilter? filter = null;
                if (savedQuery.FilterPlan is { Clauses.Length: > 0 } filterPlan)
                {
                    var compiled = FilterPlanCompiler.Compile(filterPlan, resource);
                    if (!compiled.IsSuccess || compiled.Expression is null)
                    {
                        throw new AnalysisContentValidationException(
                            compiled.ErrorMessage ?? "Saved query filter plan could not be compiled.");
                    }

                    filter = QueryFilter.FromExpression(
                        compiled.Expression,
                        new FilterSource("saved-query", FilterLanguage.Cql2Json, "AnalysisContent"));
                }

                var query = new UnifiedQuery
                {
                    Filter = filter,
                    Limit = previewLimit,
                    OutFields = savedQuery.OutFields.Count > 0
                        ? ImmutableArray.CreateRange(savedQuery.OutFields)
                        : null,
                    OutputCrs = savedQuery.OutputSrid.HasValue
                        ? QueryCrs.Create(savedQuery.OutputSrid.Value)
                        : null
                };

                var validation = queryProcessor.ValidateQuery(query, resource);
                if (!validation.IsValid)
                {
                    throw new AnalysisContentValidationException(
                        validation.ErrorMessage ?? "Saved query preview request is invalid.");
                }

                var optimized = queryProcessor.OptimizeQuery(query, resource);
                var featureQuery = queryProcessor.ToFeatureQuery(optimized, resource);
                var result = await featureReader.QueryAsync(savedQuery.LayerId, featureQuery, cancellationToken).ConfigureAwait(false);

                var artifactId = CreatePreviewArtifactId();
                var binding = new ArtifactBindingRef
                {
                    ArtifactId = artifactId,
                    SourceItemId = itemId,
                    SourceVersion = contentVersion.Version,
                    SourceVersionId = contentVersion.VersionId,
                    Role = "preview",
                    TargetKind = "map",
                    TargetSlot = "source"
                };

                var now = timeProvider.GetUtcNow();
                await store.UpsertArtifactAsync(new ResultArtifactRecord
                {
                    ArtifactId = artifactId,
                    ResultPackageId = $"{itemId}:v{contentVersion.Version}:preview",
                    JobId = "preview",
                    SourceItemId = itemId,
                    SourceVersion = contentVersion.Version,
                    SourceVersionId = contentVersion.VersionId,
                    Kind = ArtifactKind.FeatureLayer,
                    Label = $"{savedQuery.ServiceName ?? resource.Metadata.Name} preview",
                    Uri = $"honua://analysis/artifacts/{artifactId}",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["layerId"] = savedQuery.LayerId.ToString(CultureInfo.InvariantCulture),
                        ["previewLimit"] = previewLimit.ToString(CultureInfo.InvariantCulture)
                    },
                    Provenance = BuildSourceProvenance(contentVersion, savedQuery.OutputSrid, savedQuery.Units),
                    RetentionState = ResultArtifactRetentionState.Preview,
                    CreatedAt = now,
                    ExpiresAt = now.Add(PreviewRetention)
                }, cancellationToken).ConfigureAwait(false);

                Log.SavedQueryPreviewed(logger, itemId, contentVersion.Version, result.Items.Length);

                return new SavedQueryPreviewResult
                {
                    PreviewArtifactId = artifactId,
                    ItemId = itemId,
                    Version = contentVersion.Version,
                    LayerId = savedQuery.LayerId,
                    Features = result.Items.Select(SavedQueryPreviewFeature.FromFeature).ToArray(),
                    TotalCount = result.TotalCount,
                    ExceededPreviewLimit = result.HasMoreResults || result.TotalCount > previewLimit,
                    Binding = binding
                };
            }).ConfigureAwait(false);

    public async Task<AnalysisContentJobResult> SubmitAnalysisPackageAsync(
        string itemId,
        int version,
        RunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.SubmitAnalysisPackage,
            async activity =>
            {
                var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                var package = contentVersion.AnalysisPackage
                    ?? throw new AnalysisContentValidationException("The requested version is not an analysis package.");
                AnalysisContentTelemetry.SetContentTags(
                    activity,
                    contentVersion.ItemId,
                    contentVersion.Kind,
                    contentVersion.Version,
                    contentVersion.VersionId);

                var metadata = BuildJobMetadata(contentVersion, package, command.Parameters);
                var job = await geoprocessingJobService.SubmitJobAsync(
                    package.Plan,
                    command.IdempotencyKey,
                    principal,
                    metadata,
                    cancellationToken).ConfigureAwait(false);

                Log.AnalysisPackageSubmitted(logger, itemId, version, job.OperationId);
                activity?.SetTag(Honua.ServiceDefaults.HonuaTelemetry.Tags.JobId, job.OperationId);
                return new AnalysisContentJobResult(job, contentVersion);
            }).ConfigureAwait(false);

    public async Task<AnalysisContentJobResult> RerunAnalysisPackageAsync(
        string itemId,
        int version,
        RerunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.RerunAnalysisPackage,
            async activity =>
            {
                var contentVersion = await GetRequiredVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
                var package = contentVersion.AnalysisPackage
                    ?? throw new AnalysisContentValidationException("The requested version is not an analysis package.");
                AnalysisContentTelemetry.SetContentTags(
                    activity,
                    contentVersion.ItemId,
                    contentVersion.Kind,
                    contentVersion.Version,
                    contentVersion.VersionId);

                var submittedVersion = contentVersion;
                if (command.ParameterOverrides is { Count: > 0 })
                {
                    package = ApplyExecutableParameterOverrides(package, command.ParameterOverrides);
                    var next = new CreateAnalysisContentVersionCommand(
                        SavedQuery: null,
                        AnalysisPackage: package,
                        BasedOnVersionId: contentVersion.VersionId,
                        CreatedFromJobId: NormalizeOptional(command.RerunOfJobId),
                        CreatedFromArtifactIds: []);
                    var result = await AddVersionAsync(itemId, next, principal, cancellationToken).ConfigureAwait(false);
                    submittedVersion = result.Version;
                    package = submittedVersion.AnalysisPackage!;
                    AnalysisContentTelemetry.SetContentTags(
                        activity,
                        submittedVersion.ItemId,
                        submittedVersion.Kind,
                        submittedVersion.Version,
                        submittedVersion.VersionId);
                }

                var metadata = BuildJobMetadata(submittedVersion, package, runtimeParameters: null);
                if (!string.IsNullOrWhiteSpace(command.RerunOfJobId))
                {
                    metadata[AnalysisContentMetadataKeys.RerunOfJobId] = command.RerunOfJobId.Trim();
                }

                if (!string.IsNullOrWhiteSpace(command.RerunOfResultPackageId))
                {
                    metadata[AnalysisContentMetadataKeys.RerunOfResultPackageId] = command.RerunOfResultPackageId.Trim();
                }

                var job = await geoprocessingJobService.SubmitJobAsync(
                    package.Plan,
                    command.IdempotencyKey,
                    principal,
                    metadata,
                    cancellationToken).ConfigureAwait(false);

                Log.AnalysisPackageRerunSubmitted(logger, itemId, submittedVersion.Version, job.OperationId);
                activity?.SetTag(Honua.ServiceDefaults.HonuaTelemetry.Tags.JobId, job.OperationId);
                return new AnalysisContentJobResult(job, submittedVersion);
            }).ConfigureAwait(false);

    public async Task<ResultArtifactRecord> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.GetArtifact,
            async activity =>
            {
                activity?.SetTag(AnalysisContentTelemetry.Tags.ArtifactId, artifactId);
                var artifact = await store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (artifact is null)
                {
                    throw new AnalysisContentNotFoundException($"Artifact '{artifactId}' was not found.");
                }

                activity?.SetTag(AnalysisContentTelemetry.Tags.ContentItemId, artifact.SourceItemId);
                activity?.SetTag(AnalysisContentTelemetry.Tags.ContentVersion, artifact.SourceVersion);
                activity?.SetTag(AnalysisContentTelemetry.Tags.ContentVersionId, artifact.SourceVersionId);
                return artifact;
            }).ConfigureAwait(false);

    public async Task<AnalysisJobLogs> GetJobLogsAsync(
        string jobId,
        int? limit,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.GetJobLogs,
            async activity =>
            {
                var resolvedLimit = ResolveLimit(limit, DefaultLogLimit, MaxLogLimit);
                activity?.SetTag(Honua.ServiceDefaults.HonuaTelemetry.Tags.JobId, jobId);
                activity?.SetTag(AnalysisContentTelemetry.Tags.LogLimit, resolvedLimit);

                // Resolve the job through the job service first so unknown ids return not-found
                // (instead of 200 with empty logs) and the same job-read authorization enforced by
                // the failure endpoint is applied before any log content is returned.
                _ = await TranslateDiagnosticStoreFailuresAsync(
                    () => geoprocessingJobService.GetJobAsync(jobId, principal, cancellationToken))
                    .ConfigureAwait(false);

                if (_logStore is null)
                {
                    return new AnalysisJobLogs
                    {
                        JobId = jobId,
                        Entries = [],
                        TotalCount = 0,
                        Truncated = false
                    };
                }

                var tail = await TranslateDiagnosticStoreFailuresAsync(
                    () => _logStore.TailAsync(jobId, resolvedLimit, cancellationToken))
                    .ConfigureAwait(false);
                var bounded = tail.Items
                    .Select(entry => new AnalysisJobLogEntry
                    {
                        Timestamp = entry.Timestamp,
                        Level = entry.Level,
                        Message = SanitizeDiagnostic(entry.Message),
                        Phase = SanitizePhase(entry.Phase),
                        Metadata = SanitizeMetadata(entry.Metadata)
                    })
                    .ToArray();

                return new AnalysisJobLogs
                {
                    JobId = jobId,
                    Entries = bounded,
                    TotalCount = tail.TotalCount,
                    Truncated = tail.TotalCount > bounded.Length
                };
            }).ConfigureAwait(false);

    public async Task<AnalysisJobFailure> GetJobFailureAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => await AnalysisContentTelemetry.TrackAsync(
            AnalysisContentTelemetry.OperationsNames.GetJobFailure,
            async activity =>
            {
                activity?.SetTag(Honua.ServiceDefaults.HonuaTelemetry.Tags.JobId, jobId);
                var job = await TranslateDiagnosticStoreFailuresAsync(
                    () => geoprocessingJobService.GetJobAsync(jobId, principal, cancellationToken))
                    .ConfigureAwait(false);

                AnalysisJobFailure failure = job.Status switch
                {
                    ExecutionJobStatus.Failed => new AnalysisJobFailure
                    {
                        JobId = job.OperationId,
                        Classification = ClassifyFailure(job.ErrorMessage),
                        Message = SanitizeFailureMessage(job.ErrorMessage),
                        IsTerminal = true,
                        FailedAt = job.CompletedAt
                    },
                    ExecutionJobStatus.Cancelled => new AnalysisJobFailure
                    {
                        JobId = job.OperationId,
                        Classification = AnalysisJobFailureClassification.Cancelled,
                        Message = "The analysis job was cancelled.",
                        IsTerminal = true,
                        FailedAt = job.CompletedAt
                    },
                    _ => throw new AnalysisContentConflictException("The requested job has not failed.")
                };

                activity?.SetTag("honua.analysis_content.failure_classification", failure.Classification.ToString());
                return failure;
            }).ConfigureAwait(false);

    /// <summary>
    /// Runs a job/log diagnostic read and maps backing job- or log-store outages to
    /// <see cref="AnalysisContentStoreUnavailableException"/> so an unavailable Redis-backed store
    /// surfaces as the documented retryable 503 instead of a generic 500. Job-read authorization
    /// and not-found results are raised by the read itself and pass through unchanged, as does
    /// cancellation.
    /// </summary>
    private static async Task<T> TranslateDiagnosticStoreFailuresAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException ex)
        {
            throw new AnalysisContentStoreUnavailableException(
                "The analysis job or log store is currently unavailable.",
                ex);
        }
        catch (RedisException ex)
        {
            throw new AnalysisContentStoreUnavailableException(
                "The analysis job or log store is currently unavailable.",
                ex);
        }
    }

    private async Task<AnalysisContentItem> GetRequiredItemAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var item = await store.GetItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return item ?? throw new AnalysisContentNotFoundException($"Analysis content item '{itemId}' was not found.");
    }

    private async Task<AnalysisContentVersion> GetRequiredVersionAsync(
        string itemId,
        int? version,
        CancellationToken cancellationToken)
    {
        var contentVersion = await store.GetVersionAsync(itemId, version, cancellationToken).ConfigureAwait(false);
        return contentVersion ?? throw new AnalysisContentNotFoundException(
            version.HasValue
                ? $"Analysis content item '{itemId}' version '{version.Value}' was not found."
                : $"Analysis content item '{itemId}' has no versions.");
    }

    private static void ValidateCreate(CreateAnalysisContentItemCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new AnalysisContentValidationException("Analysis content name is required.");
        }

        ValidatePayload(command.Kind, command.SavedQuery, command.AnalysisPackage);
    }

    private static void ValidatePayload(
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage)
    {
        switch (kind)
        {
            case AnalysisContentKind.SavedQuery:
                if (savedQuery is null || analysisPackage is not null)
                {
                    throw new AnalysisContentValidationException("Saved-query content requires a savedQuery payload only.");
                }

                if (savedQuery.LayerId < 0)
                {
                    throw new AnalysisContentValidationException("Saved-query layerId must be non-negative.");
                }

                break;
            case AnalysisContentKind.AnalysisPackage:
                if (analysisPackage is null || savedQuery is not null)
                {
                    throw new AnalysisContentValidationException("Analysis-package content requires an analysisPackage payload only.");
                }

                if (analysisPackage.Plan.Steps.Count == 0)
                {
                    throw new AnalysisContentValidationException("Analysis package plan must contain at least one step.");
                }

                break;
            default:
                throw new AnalysisContentValidationException("Unsupported analysis content kind.");
        }
    }

    private static AnalysisPackageContent ApplyExecutableParameterOverrides(
        AnalysisPackageContent package,
        IReadOnlyDictionary<string, string> overrides)
    {
        var normalizedOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new AnalysisContentValidationException("Parameter override keys are required.");
            }

            normalizedOverrides[pair.Key.Trim()] = pair.Value;
        }

        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);
        var updatedSteps = new AnalysisPlanStep[package.Plan.Steps.Count];
        for (var index = 0; index < package.Plan.Steps.Count; index++)
        {
            var step = package.Plan.Steps[index];
            if (step.Inputs.Count == 0)
            {
                updatedSteps[index] = step;
                continue;
            }

            Dictionary<string, string>? updatedInputs = null;
            foreach (var pair in normalizedOverrides)
            {
                if (!step.Inputs.ContainsKey(pair.Key))
                {
                    continue;
                }

                updatedInputs ??= new Dictionary<string, string>(step.Inputs, StringComparer.Ordinal);
                updatedInputs[pair.Key] = pair.Value;
                matchedKeys.Add(pair.Key);
            }

            updatedSteps[index] = updatedInputs is null
                ? step
                : step with { Inputs = updatedInputs };
        }

        foreach (var pair in normalizedOverrides)
        {
            if (!matchedKeys.Contains(pair.Key))
            {
                throw new AnalysisContentValidationException(
                    $"Parameter override '{pair.Key}' does not match an executable analysis plan input.");
            }
        }

        var mergedParameters = new Dictionary<string, string>(package.Parameters, StringComparer.Ordinal);
        foreach (var pair in normalizedOverrides)
        {
            mergedParameters[pair.Key] = pair.Value;
        }

        return package with
        {
            Parameters = mergedParameters,
            Plan = package.Plan with { Steps = updatedSteps }
        };
    }

    private static AnalysisContentVersion CreateVersion(
        string itemId,
        int version,
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage,
        string? basedOnVersionId,
        string? createdFromJobId,
        IReadOnlyList<string> createdFromArtifactIds,
        string? createdBy,
        DateTimeOffset now)
    {
        ValidatePayload(kind, savedQuery, analysisPackage);
        return new AnalysisContentVersion
        {
            VersionId = $"{itemId}:v{version.ToString(CultureInfo.InvariantCulture)}",
            ItemId = itemId,
            Version = version,
            Kind = kind,
            SavedQuery = savedQuery,
            AnalysisPackage = analysisPackage,
            ContentHash = ComputeContentHash(kind, savedQuery, analysisPackage),
            BasedOnVersionId = basedOnVersionId,
            CreatedFromJobId = createdFromJobId,
            CreatedFromArtifactIds = createdFromArtifactIds,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    private static string ComputeContentHash(
        AnalysisContentKind kind,
        SavedQueryContent? savedQuery,
        AnalysisPackageContent? analysisPackage)
    {
        byte[] payload = kind == AnalysisContentKind.SavedQuery
            ? JsonSerializer.SerializeToUtf8Bytes(savedQuery, AnalysisContentJsonContext.Default.SavedQueryContent)
            : JsonSerializer.SerializeToUtf8Bytes(analysisPackage, AnalysisContentJsonContext.Default.AnalysisPackageContent);

        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildJobMetadata(
        AnalysisContentVersion version,
        AnalysisPackageContent package,
        IReadOnlyDictionary<string, string>? runtimeParameters)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AnalysisContentMetadataKeys.ItemId] = version.ItemId,
            [AnalysisContentMetadataKeys.Version] = version.Version.ToString(CultureInfo.InvariantCulture),
            [AnalysisContentMetadataKeys.VersionId] = version.VersionId,
            [AnalysisContentMetadataKeys.Kind] = version.Kind.ToString(),
        };

        if (package.SpatialReferenceId.HasValue)
        {
            metadata[AnalysisContentMetadataKeys.SourceSrid] =
                package.SpatialReferenceId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(package.Units))
        {
            metadata[AnalysisContentMetadataKeys.SourceUnits] = package.Units.Trim();
        }

        foreach (var pair in package.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                metadata[$"analysis.content.parameter.{pair.Key}"] = pair.Value;
            }
        }

        if (runtimeParameters is not null)
        {
            foreach (var pair in runtimeParameters)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    metadata[$"analysis.content.runtime_parameter.{pair.Key}"] = pair.Value;
                }
            }
        }

        return metadata;
    }

    private static Dictionary<string, string> BuildSourceProvenance(
        AnalysisContentVersion version,
        int? srid,
        string? units)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AnalysisContentMetadataKeys.ItemId] = version.ItemId,
            [AnalysisContentMetadataKeys.Version] = version.Version.ToString(CultureInfo.InvariantCulture),
            [AnalysisContentMetadataKeys.VersionId] = version.VersionId,
            [AnalysisContentMetadataKeys.Kind] = version.Kind.ToString()
        };

        if (srid.HasValue)
        {
            provenance[AnalysisContentMetadataKeys.SourceSrid] = srid.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(units))
        {
            provenance[AnalysisContentMetadataKeys.SourceUnits] = units.Trim();
        }

        return provenance;
    }

    private static int ResolveLimit(int? limit, int defaultLimit, int maxLimit)
    {
        var resolved = limit.GetValueOrDefault(defaultLimit);
        if (resolved <= 0)
        {
            throw new AnalysisContentValidationException("Limit must be greater than zero.");
        }

        return Math.Min(resolved, maxLimit);
    }

    private static AnalysisJobFailureClassification ClassifyFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AnalysisJobFailureClassification.Unknown;
        }

        if (message.Contains("validation", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.ValidationFailed;
        }

        if (message.Contains("unauthor", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.AuthorizationDenied;
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.TimedOut;
        }

        if (message.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("output", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisJobFailureClassification.ArtifactOutputFailed;
        }

        return AnalysisJobFailureClassification.ExecutionFailed;
    }

    private static string SanitizeFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The analysis job failed.";
        }

        var sanitized = SanitizeDiagnostic(message);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "The analysis job failed."
            : sanitized;
    }

    private static readonly string[] SensitiveDiagnosticMarkers =
    [
        "Npgsql",
        "connection string",
        "password",
        "pwd=",
        "secret",
        "token",
        "credential",
        "api key",
        "apikey",
        "api_key",
        "bearer",
        "private key"
    ];

    private static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.ReplaceLineEndings(" ").Trim();
        if (ContainsSensitiveMarker(sanitized))
        {
            return "The analysis job failed. See server logs for details.";
        }

        return sanitized.Length <= MaxDiagnosticLength
            ? sanitized
            : sanitized[..MaxDiagnosticLength];
    }

    private static bool ContainsSensitiveMarker(string value)
    {
        // " at " (case-sensitive) catches stack-trace frames; the remaining markers catch
        // provider internals and secret-bearing fragments (secret/token/credential/api key/
        // bearer) regardless of case, including values embedded in otherwise innocuous keys.
        if (value.Contains(" at ", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var marker in SensitiveDiagnosticMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? SanitizePhase(string? value)
    {
        var sanitized = SanitizeDiagnostic(value);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private static Dictionary<string, string>? SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        return metadata
            .Where(pair => !IsSensitiveMetadataKey(pair.Key))
            .Take(20)
            .ToDictionary(pair => pair.Key, pair => SanitizeDiagnostic(pair.Value), StringComparer.Ordinal);
    }

    // Metadata key fragments that imply a secret value. Value-only inspection (SanitizeDiagnostic)
    // cannot distinguish an opaque secret such as {"token":"abc123"} from ordinary data, so any
    // entry whose key name matches is dropped wholesale. Mirrors the secret-key check used by the
    // sibling console job viewer while preserving the original password/secret/connection coverage.
    private static readonly string[] SensitiveMetadataKeyMarkers =
    [
        "password",
        "pwd",
        "secret",
        "connection",
        "token",
        "credential",
        "api key",
        "apikey",
        "api_key",
        "bearer",
        "private key"
    ];

    private static bool IsSensitiveMetadataKey(string key)
    {
        foreach (var marker in SensitiveMetadataKeyMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateItemId() => $"analysis-content-{Guid.NewGuid():N}";

    private static string CreatePreviewArtifactId() => $"analysis-preview-{Guid.NewGuid():N}";

    private static string NormalizeName(string value) => value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveActor(ClaimsPrincipal principal)
        => principal.Identity?.Name
           ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? principal.FindFirstValue("sub");

    private static partial class Log
    {
        [LoggerMessage(12001, LogLevel.Information, "Created analysis content item {ItemId} kind {Kind} version {Version}")]
        public static partial void ContentItemCreated(ILogger logger, string itemId, string kind, int version);

        [LoggerMessage(12002, LogLevel.Information, "Created analysis content item {ItemId} version {Version}")]
        public static partial void ContentVersionCreated(ILogger logger, string itemId, int version);

        [LoggerMessage(12003, LogLevel.Information, "Previewed saved query {ItemId} version {Version} with {FeatureCount} features")]
        public static partial void SavedQueryPreviewed(ILogger logger, string itemId, int version, int featureCount);

        [LoggerMessage(12004, LogLevel.Information, "Submitted analysis package {ItemId} version {Version} as job {JobId}")]
        public static partial void AnalysisPackageSubmitted(ILogger logger, string itemId, int version, string jobId);

        [LoggerMessage(12005, LogLevel.Information, "Submitted analysis package rerun {ItemId} version {Version} as job {JobId}")]
        public static partial void AnalysisPackageRerunSubmitted(ILogger logger, string itemId, int version, string jobId);

        [LoggerMessage(12006, LogLevel.Debug, "Retrying analysis content version creation for {ItemId} after conflict on attempt {Attempt}")]
        public static partial void ContentVersionConflictRetry(ILogger logger, string itemId, int attempt);

        [LoggerMessage(12007, LogLevel.Debug, "Analysis content estimate could not resolve statistics for layer {LayerId}; reporting a lower bound")]
        public static partial void EstimateLayerUnresolved(ILogger logger, int layerId, Exception exception);
    }
}
