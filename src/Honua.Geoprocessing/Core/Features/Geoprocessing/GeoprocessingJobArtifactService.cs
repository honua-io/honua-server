// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

internal sealed class GeoprocessingJobArtifactService
{
    internal const string TypedRasterExecutionNotSupportedCode = "RASTER_SOURCE_REFERENCE_REQUIRES_RESOLUTION";
    private const string TypedRasterExecutionNotSupportedMessage =
        "Direct durable raster references require authenticated server-side resolution; "
        + "use the catalog layerId/rasterId path. Bounded inline raster sources remain supported.";

    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;
    private readonly IProcessCatalog _processCatalog;
    private readonly IGeoprocessingResultPackageStore? _resultPackageStore;
    private readonly IGeoprocessingRasterSourceResolver? _rasterSourceResolver;
    private readonly GeoprocessingRasterOutputRegistrar? _outputRegistrar;
    private readonly IGeoprocessingOutputObjectStore? _outputStore;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTerminalCallback[] _terminalCallbacks;

    public GeoprocessingJobArtifactService(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IProcessCatalog processCatalog,
        IGeoprocessingResultPackageStore? resultPackageStore = null,
        IGeoprocessingRasterSourceResolver? rasterSourceResolver = null,
        GeoprocessingRasterOutputRegistrar? outputRegistrar = null,
        IGeoprocessingOutputObjectStore? outputStore = null,
        TimeProvider? timeProvider = null,
        IEnumerable<IJobTerminalCallback>? terminalCallbacks = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _processCatalog = processCatalog;
        _resultPackageStore = resultPackageStore;
        _rasterSourceResolver = rasterSourceResolver;
        _outputRegistrar = outputRegistrar;
        _outputStore = outputStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _terminalCallbacks = terminalCallbacks?.ToArray() ?? [];
    }

    // Replay is skipped once the result package is stored, and callbacks ignore the caller's token so a cancelled read cannot leave a replay half-done.
    internal async Task ReplayTerminalCallbacksAsync(ExecutionJobRecord job)
    {
        if (_terminalCallbacks.Length == 0
            || await HasStoredResultPackageAsync(job).ConfigureAwait(false))
        {
            return;
        }

        GeoprocessingStoreUnavailableException? retryable = null;
        foreach (var callback in _terminalCallbacks)
        {
            try
            {
                if (callback is IReplayableJobTerminalCallback replayable)
                {
                    await replayable.ReplayAsync(job, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await callback.OnTerminalAsync(job, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (GeoprocessingStoreUnavailableException ex) when (callback is IReplayableJobTerminalCallback)
            {
                GeoprocessingServiceLog.TerminalCallbackReplayFailed(_logger, job.OperationId, ex);
                retryable ??= ex;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                GeoprocessingServiceLog.TerminalCallbackReplayFailed(_logger, job.OperationId, ex);
            }
        }

        if (retryable is not null)
        {
            throw retryable;
        }
    }

    private async Task<bool> HasStoredResultPackageAsync(ExecutionJobRecord job)
    {
        if (_resultPackageStore is null)
        {
            return false;
        }

        try
        {
            var stored = await _resultPackageStore
                .GetAsync(job.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
            return stored is not null
                && string.Equals(
                    stored.ResultPackageId,
                    GeoprocessingResultPackageFactory.CreateResultPackageId(job),
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unknown is treated as missing: the replay is idempotent and non-destructive.
            GeoprocessingServiceLog.JobResultsStoreReadFailed(_logger, job.OperationId, ex);
            return false;
        }
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    public void ValidateRasterSources(AnalysisPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var failures = GetRasterSourceValidationFailures(plan, cancellationToken);
        if (failures.Count > 0)
        {
            var failure = failures[0];
            throw new GeoprocessingValidationException(
                $"Raster source plan is invalid ({failure.Code}): {failure.Message}");
        }
    }

    public IReadOnlyList<GeoprocessingValidationFailure> GetRasterSourceValidationFailures(
        AnalysisPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var options = CreateRasterSourceValidationOptions();
        var validation = RasterSourcePlanValidator.Validate(
            plan,
            options,
            cancellationToken);

        var failures = validation.Errors
            .Select(error => new GeoprocessingValidationFailure
            {
                Code = error.Code,
                Message = error.Message,
                FieldPath = error.Field,
            })
            .ToList();

        if (plan.Steps is null || failures.Count > 0)
        {
            return failures;
        }

        var inspectedSources = 0;
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step?.RasterSources is null)
            {
                continue;
            }

            var definition = step.Kind != AnalysisPlanStepKind.Geoprocess
                || string.IsNullOrWhiteSpace(step.ProcessId)
                ? null
                : _processCatalog.GetProcess(step.ProcessId);
            foreach (var source in step.RasterSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inspectedSources++ >= options.MaxSourcesPerPlan)
                {
                    return failures;
                }

                var fieldPath = $"steps[{step.StepId}].raster_sources.{source.Key}";
                if (step.Inputs?.ContainsKey(source.Key) == true)
                {
                    failures.Add(new GeoprocessingValidationFailure
                    {
                        Code = RasterSourceValidationCodes.InvalidParameterBinding,
                        Message = $"Step '{step.StepId}' supplies raster input '{source.Key}' as both a typed "
                            + "reference and a legacy string input. Supply exactly one source representation.",
                        FieldPath = fieldPath,
                    });
                    continue;
                }

                var parameter = definition?.Parameters.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, source.Key, StringComparison.Ordinal));
                if (step.Kind != AnalysisPlanStepKind.Geoprocess
                    || definition is null
                    || parameter?.AcceptsRasterSource != true)
                {
                    failures.Add(new GeoprocessingValidationFailure
                    {
                        Code = RasterSourceValidationCodes.InvalidParameterBinding,
                        Message = $"Step '{step.StepId}' raster source '{source.Key}' is invalid: typed raster "
                            + "sources require a geoprocess step whose catalog process explicitly declares "
                            + "that parameter as a raster source input.",
                        FieldPath = fieldPath,
                    });
                }
            }
        }

        return failures;
    }

    private RasterSourceValidationOptions CreateRasterSourceValidationOptions() => new()
    {
        MaxInlineBytes = _executorOptions.CurrentValue.MaxInlineRasterSourceBytes,
        MaxSourcesPerPlan = _executorOptions.CurrentValue.MaxRasterSourcesPerPlan,
        MaxParameterNameLength = _executorOptions.CurrentValue.MaxRasterSourceParameterNameLength,
        MaxSerializedBytesPerPlan = _executorOptions.CurrentValue.MaxRasterSourceSerializedBytesPerPlan,
    };

    public static void EnsureTypedRasterExecutionSupported(AnalysisPlan plan)
    {
        var violation = GetTypedRasterExecutionViolation(plan);
        if (violation is not null)
        {
            throw new GeoprocessingValidationException($"{violation.Code}: {violation.Message}");
        }
    }

    public static GeoprocessingValidationFailure? GetTypedRasterExecutionViolation(AnalysisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var step in plan.Steps)
        {
            if (step.RasterSources?.Any(source => source.Value is not InlineRasterSourceDescriptor) == true)
            {
                return new GeoprocessingValidationFailure
                {
                    Code = TypedRasterExecutionNotSupportedCode,
                    Message = TypedRasterExecutionNotSupportedMessage,
                    FieldPath = $"steps[{step.StepId}].raster_sources",
                };
            }
        }

        return null;
    }

    // Raster selectors are bound before the layer gate, and that bind does not read raster bytes.
    public Task<AnalysisPlan> BindRasterSourceLayerIdsAsync(
        AnalysisPlan plan,
        CancellationToken cancellationToken)
        => GeoprocessingRasterSourceResolution.BindLayerIdsAsync(
            plan, _processCatalog, _rasterSourceResolver, cancellationToken);

    public Task<AnalysisPlan> ResolveRasterSourcesAsync(
        AnalysisPlan plan,
        JobSecurityContext submitterSecurityContext,
        string jobId,
        CancellationToken cancellationToken)
        => GeoprocessingRasterSourceResolution.ResolveAsync(
            plan,
            _processCatalog,
            _rasterSourceResolver,
            new RasterSecurityContextReference
            {
                TenantId = CreateOpaqueTenantReference(submitterSecurityContext.TenantId),
                AuthorizationSnapshotReference = $"job:{jobId}:submitter",
            },
            cancellationToken);

    // The tenant id is not copied onto the worker contract because lifecycle identifiers can contain characters the raster descriptor alphabet rejects.
    internal static string CreateOpaqueTenantReference(string? tenantId)
    {
        var source = tenantId is null ? "tenant:none" : $"tenant:named:{tenantId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"tenant:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // Retention is checked from completion time before a cached or synthesized package is returned, so an expired package is not rebuilt from a job record that outlives it.
    public async Task<AnalysisResultPackage> GetOrSynthesizeResultPackageAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var jobId = job.OperationId;
        EnsureResultsRetained(job);
        var expectedResultPackageId = GeoprocessingResultPackageFactory.CreateResultPackageId(job);
        if (_resultPackageStore != null)
        {
            try
            {
                var storedPackage = await _resultPackageStore
                    .GetAsync(jobId, cancellationToken)
                    .ConfigureAwait(false);
                if (storedPackage != null &&
                    string.Equals(
                        storedPackage.ResultPackageId,
                        expectedResultPackageId,
                        StringComparison.Ordinal))
                {
                    GeoprocessingServiceLog.JobResultsRetrieved(_logger, jobId);
                    return ProjectStagedArtifactAvailability(storedPackage, jobId, _outputStore);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                GeoprocessingServiceLog.JobResultsStoreReadFailed(_logger, jobId, ex);
            }
        }

        var synthesizedPackage = GeoprocessingResultPackageFactory.Create(job, _processCatalog);

        // Registration is atomic with result visibility (#3089, ADR-0071): a job with
        // registration intents never yields a Completed package until every intent is
        // durably registered. The registrar is idempotent, so a crash between the
        // terminal transition and this read is healed by replaying it here.
        if (GeoprocessingRasterOutputRegistrar.HasRegistrationIntents(job))
        {
            if (_outputRegistrar is null)
            {
                throw new InvalidOperationException(
                    $"Job '{jobId}' declares raster output registration intents, but no output registrar "
                    + "is configured in this deployment.");
            }

            synthesizedPackage = await _outputRegistrar
                .EnsureRegisteredAsync(job, synthesizedPackage, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_resultPackageStore != null)
        {
            try
            {
                await _resultPackageStore
                    .SetAsync(jobId, synthesizedPackage, ProgressRetention, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                GeoprocessingServiceLog.JobResultsStoreWriteFailed(_logger, jobId, ex);
            }
        }

        GeoprocessingServiceLog.JobResultsRetrieved(_logger, jobId);
        return ProjectStagedArtifactAvailability(synthesizedPackage, jobId, _outputStore);
    }

    private void EnsureResultsRetained(ExecutionJobRecord job)
    {
        if (!GeoprocessingResultRetention.HasExpired(job, _timeProvider.GetUtcNow(), ProgressRetention))
        {
            return;
        }

        GeoprocessingServiceLog.JobResultsExpired(_logger, job.OperationId);
        throw new GeoprocessingNotFoundException(
            $"Results for job '{job.OperationId}' are no longer available; the result retention period has elapsed.");
    }

    // Staged routes are advertised only when this host has the output store, so a split-host topology does not publish a route that always returns 503.
    internal static AnalysisResultPackage ProjectStagedArtifactAvailability(
        AnalysisResultPackage package,
        string jobId,
        IGeoprocessingOutputObjectStore? outputStore)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        ArtifactRef[]? projected = null;
        for (var index = 0; index < package.Artifacts.Count; index++)
        {
            var artifact = package.Artifacts[index];
            if (!artifact.Metadata.TryGetValue(RasterOutputArtifactMetadata.Staged, out var staged)
                || !string.Equals(staged, "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var canServe = RasterOutputContentRoutes.CanServe(
                    outputStore,
                    artifact.Metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreProvider),
                    artifact.Metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreReference));
            var uri = canServe ? RasterOutputContentRoutes.BuildRelative(jobId, index) : null;
            var hasProjectedRoute = artifact.Metadata.TryGetValue(
                RasterOutputArtifactMetadata.ContentRoute,
                out var projectedRoute);
            var metadataIsCurrent = canServe
                ? hasProjectedRoute && string.Equals(projectedRoute, uri, StringComparison.Ordinal)
                : !hasProjectedRoute;
            if (string.Equals(uri, artifact.Uri, StringComparison.Ordinal) && metadataIsCurrent)
            {
                continue;
            }

            var metadata = artifact.Metadata.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            if (canServe)
            {
                metadata[RasterOutputArtifactMetadata.ContentRoute] = uri!;
            }
            else
            {
                metadata.Remove(RasterOutputArtifactMetadata.ContentRoute);
            }

            projected ??= package.Artifacts.ToArray();
            projected[index] = artifact with { Uri = uri, Metadata = metadata };
        }

        return projected is null ? package : package with { Artifacts = projected };
    }
}
