// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Materializes geoprocessing job artifacts for <see cref="GeoprocessingJobService"/>:
/// resolving native raster <em>input</em> sources from the catalog at submit time and
/// reading or synthesizing the result-package <em>output</em> at completion. Owns the
/// optional <see cref="IGeoprocessingRasterSourceResolver"/> and
/// <see cref="IGeoprocessingResultPackageStore"/> collaborators (both default-off when the
/// supporting infrastructure is absent) plus the shared <see cref="IProcessCatalog"/> used
/// by both flows. Behavior, logging, and the retention TTL are identical to the inline
/// logic the service previously performed.
/// </summary>
internal sealed class GeoprocessingJobArtifactService
{
    internal const string TypedRasterExecutionNotSupportedCode = "RASTER_SOURCE_EXECUTION_NOT_SUPPORTED";
    private const string TypedRasterExecutionNotSupportedMessage =
        "Typed raster source execution is disabled until #3090 adds authenticated v2 worker resolution; "
        + "use the existing catalog layerId/rasterId path.";

    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;
    private readonly IProcessCatalog _processCatalog;
    private readonly IGeoprocessingResultPackageStore? _resultPackageStore;
    private readonly IGeoprocessingRasterSourceResolver? _rasterSourceResolver;

    /// <summary>
    /// Creates the artifact coordinator over the catalog, the optional result-package store,
    /// and the optional raster-source resolver.
    /// </summary>
    public GeoprocessingJobArtifactService(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IProcessCatalog processCatalog,
        IGeoprocessingResultPackageStore? resultPackageStore = null,
        IGeoprocessingRasterSourceResolver? rasterSourceResolver = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _processCatalog = processCatalog;
        _resultPackageStore = resultPackageStore;
        _rasterSourceResolver = rasterSourceResolver;
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    /// <summary>
    /// Validates typed raster descriptors and parameter bindings before an approval proposal
    /// or job record can be persisted.
    /// </summary>
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

    /// <summary>
    /// Returns the configured typed-raster contract and parameter-binding failures used by
    /// submission so read-only plan preflight can report the same field-specific diagnostics.
    /// </summary>
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
                if (step.Inputs?.TryGetValue(source.Key, out var legacyValue) == true
                    && !string.IsNullOrWhiteSpace(legacyValue))
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

    /// <summary>
    /// Refuses typed raster execution until the v2 worker admission/resolution path from #3090
    /// is available. Contract authoring and spec projection remain testable, but no local or
    /// remote backend can receive a descriptor it does not understand.
    /// </summary>
    public static void EnsureTypedRasterExecutionSupported(AnalysisPlan plan)
    {
        var violation = GetTypedRasterExecutionViolation(plan);
        if (violation is not null)
        {
            throw new GeoprocessingValidationException(
                $"{violation.Code}: {violation.Message}");
        }
    }

    /// <summary>
    /// Returns the structured pre-flight violation that mirrors the submit-time typed-raster
    /// execution gate, or <see langword="null"/> when the plan uses only executable inputs.
    /// </summary>
    public static GeoprocessingValidationFailure? GetTypedRasterExecutionViolation(AnalysisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            if (step.RasterSources is not { Count: > 0 })
            {
                continue;
            }

            return new GeoprocessingValidationFailure
            {
                Code = TypedRasterExecutionNotSupportedCode,
                Message = TypedRasterExecutionNotSupportedMessage,
                FieldPath = $"steps[{step.StepId}].raster_sources",
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves any native raster/surface step that references a registered catalog raster
    /// by layerId/rasterId, materializing the bytes onto the canonical base64 <c>source</c>
    /// input the worker reads (#2264). Returns the original plan unchanged when no step
    /// requires resolution.
    /// </summary>
    public Task<AnalysisPlan> ResolveRasterSourcesAsync(AnalysisPlan plan, CancellationToken cancellationToken)
        => GeoprocessingRasterSourceResolution.ResolveAsync(
            plan,
            _processCatalog,
            _rasterSourceResolver,
            _executorOptions.CurrentValue.MaxArtifactBytes,
            cancellationToken);

    /// <summary>
    /// Returns the result package for a terminal job: the durable stored package when it
    /// matches the expected identifier, otherwise a freshly synthesized package (persisted
    /// best-effort for subsequent reads). Store read/write failures are logged and
    /// swallowed so retrieval always succeeds from terminal job state.
    /// </summary>
    public async Task<AnalysisResultPackage> GetOrSynthesizeResultPackageAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var jobId = job.OperationId;
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
                    return storedPackage;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                GeoprocessingServiceLog.JobResultsStoreReadFailed(_logger, jobId, ex);
            }
        }

        var synthesizedPackage = GeoprocessingResultPackageFactory.Create(job, _processCatalog);

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
        return synthesizedPackage;
    }
}
