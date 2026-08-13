// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Authorization.Domain;
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

    /// <summary>
    /// Creates the artifact coordinator over the catalog, the optional result-package store,
    /// the optional raster-source resolver, and the optional output registrar (#3089).
    /// </summary>
    public GeoprocessingJobArtifactService(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IProcessCatalog processCatalog,
        IGeoprocessingResultPackageStore? resultPackageStore = null,
        IGeoprocessingRasterSourceResolver? rasterSourceResolver = null,
        GeoprocessingRasterOutputRegistrar? outputRegistrar = null,
        IGeoprocessingOutputObjectStore? outputStore = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _processCatalog = processCatalog;
        _resultPackageStore = resultPackageStore;
        _rasterSourceResolver = rasterSourceResolver;
        _outputRegistrar = outputRegistrar;
        _outputStore = outputStore;
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

    /// <summary>
    /// Refuses client-supplied durable descriptors before they can run under worker credentials.
    /// Authenticated catalog selectors are resolved to descriptors later in the submit pipeline;
    /// bounded typed inline sources contain no ambient object-store authority and remain valid.
    /// </summary>
    public static void EnsureTypedRasterExecutionSupported(
        AnalysisPlan plan,
        string? executionOwnedStagedAuthorizationReference = null)
    {
        var violation = GetTypedRasterExecutionViolation(plan, executionOwnedStagedAuthorizationReference);
        if (violation is not null)
        {
            throw new GeoprocessingValidationException($"{violation.Code}: {violation.Message}");
        }
    }

    /// <summary>Returns a structured violation for a client-supplied durable raster descriptor.</summary>
    public static GeoprocessingValidationFailure? GetTypedRasterExecutionViolation(
        AnalysisPlan plan,
        string? executionOwnedStagedAuthorizationReference = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var step in plan.Steps)
        {
            if (step.RasterSources?.Any(source =>
                    source.Value is not InlineRasterSourceDescriptor
                    && (source.Value is not StagedArtifactRasterSourceDescriptor staged
                        || executionOwnedStagedAuthorizationReference is null
                        || !string.Equals(
                            staged.SecurityContext.AuthorizationSnapshotReference,
                            executionOwnedStagedAuthorizationReference,
                            StringComparison.Ordinal))) == true)
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

    /// <summary>
    /// Binds raster-id selectors to their owning layer before the shared submit-time layer gate.
    /// This is metadata-only and therefore cannot read or expose raster bytes before access is
    /// authorized.
    /// </summary>
    public Task<AnalysisPlan> BindRasterSourceLayerIdsAsync(
        AnalysisPlan plan,
        CancellationToken cancellationToken)
        => GeoprocessingRasterSourceResolution.BindLayerIdsAsync(
            plan, _processCatalog, _rasterSourceResolver, cancellationToken);

    /// <summary>
    /// Resolves any native raster/surface step that references a registered catalog raster
    /// by layerId/rasterId to an immutable descriptor. The serving process performs no full
    /// object read and never places raster bytes in the durable job record (#2264/#3090).
    /// </summary>
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

    /// <summary>
    /// Produces a stable descriptor-safe reference without placing an unrestricted tenant id
    /// onto the worker contract. Tenant lifecycle identifiers may contain spaces, Unicode, or
    /// punctuation that the deliberately narrow raster descriptor alphabet rejects.
    /// </summary>
    internal static string CreateOpaqueTenantReference(string? tenantId)
    {
        var source = tenantId is null ? "tenant:none" : $"tenant:named:{tenantId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"tenant:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

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

    /// <summary>
    /// Projects canonical staged-content routes only when this serving host has the
    /// matching output store. Every protocol consumes this package, so performing the
    /// availability check here prevents gRPC, MCP, and workflow consumers from
    /// advertising a route that is guaranteed to return 503 in a split-host topology.
    /// </summary>
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
