// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
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
