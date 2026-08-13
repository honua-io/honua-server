// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Security.Cryptography;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile dispatcher for <see cref="ExecutionJobKind.Geoprocessing"/>
/// jobs inside the GDAL worker image. Mirrors the lean
/// <c>GeoprocessingDispatchJobExecutor</c> pattern: the worker host registers a
/// single <see cref="IJobExecutor"/> per <see cref="ExecutionJobKind"/>, and this
/// dispatcher routes a claimed job to the matching GDAL-backed handler by process
/// id.
///
/// It overrides <see cref="AcceptedRuntimeProfiles"/> to <c>{ "native" }</c> so the
/// substrate claim fence (this branch's <see cref="RuntimeProfiles"/> helper) keeps
/// the lean managed worker from claiming native-profile jobs and keeps this worker
/// from claiming managed-profile jobs.
/// </summary>
internal sealed partial class GdalDispatchJobExecutor : IJobExecutor
{
    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    private readonly FrozenDictionary<string, IProcessExecutor> _handlers;
    private readonly ILogger<GdalDispatchJobExecutor> _logger;
    private readonly Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? _outputStore;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingOutputStagingOptions>? _stagingOptions;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<GdalWorkerOptions>? _workerOptions;

    /// <summary>
    /// Composes the dispatcher over the auto-registered GDAL-backed executors
    /// (issue #2122). Each <see cref="IProcessExecutor"/> self-declares the process
    /// ids it handles through <see cref="IProcessExecutor.ProcessIds"/> — including
    /// the <c>surface.*</c> and <c>raster.statistics/histogram</c> families that fan
    /// a single executor over several ids — so the O(1) routing table is built from
    /// a single DI scan via <see cref="ProcessExecutorRouteTable.Build"/> instead of
    /// a hand-maintained constructor naming every executor and its id set.
    /// When an output staging store is registered (#3089), the dispatcher wraps the
    /// execution context in <see cref="GdalStagedOutputContext"/> so the shared
    /// publisher stages large outputs by reference instead of inlining them.
    /// </summary>
    public GdalDispatchJobExecutor(
        IEnumerable<IProcessExecutor> executors,
        ILogger<GdalDispatchJobExecutor> logger,
        Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? outputStore = null,
        Microsoft.Extensions.Options.IOptionsMonitor<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingOutputStagingOptions>? stagingOptions = null,
        Microsoft.Extensions.Options.IOptionsMonitor<GdalWorkerOptions>? workerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(logger);

        _handlers = ProcessExecutorRouteTable.Build(executors);
        _logger = logger;
        _outputStore = outputStore;
        _stagingOptions = stagingOptions;
        _workerOptions = workerOptions;
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

    /// <summary>Process ids this dispatcher can route.</summary>
    public IReadOnlyCollection<string> SupportedProcessIds => _handlers.Keys;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var processId = GdalJobInputReader.ResolveProcessId(job.Spec.Parameters);

        if (string.IsNullOrWhiteSpace(processId) || !_handlers.TryGetValue(processId, out var handler))
        {
            var supported = string.Join(", ", _handlers.Keys.OrderBy(id => id, StringComparer.Ordinal));
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not supported by the GDAL worker runtime. " +
                $"Supported ids: {supported}.");
        }

        var staging = _stagingOptions?.CurrentValue;
        if (_outputStore is not null && staging is { Enabled: true })
        {
            context = new GdalStagedOutputContext(context, job, _outputStore, staging);
        }

        var hydratedWorkspace = default(string);
        try
        {
            var hydration = await TryHydrateStagedRasterSourcesAsync(job, cancellationToken).ConfigureAwait(false);
            hydratedWorkspace = hydration.Workspace;
            if (hydration.Failure is not null)
            {
                return JobExecutionResult.Failed(hydration.Failure);
            }

            job = hydration.Job;
            return await handler.ExecuteAsync(job, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (hydratedWorkspace is not null)
            {
                GdalScratch.TryCleanup(hydratedWorkspace, _logger);
            }
        }
    }

    private async Task<StagedRasterHydration> TryHydrateStagedRasterSourcesAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        List<(string ParameterKey, StagedArtifactRasterSourceDescriptor Descriptor)>? stagedSources = null;
        foreach (var (key, value) in job.Spec.Parameters)
        {
            if (!key.StartsWith(GdalWorkerParameterKeys.StepRasterSourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (RasterSourceJson.Deserialize(value) is StagedArtifactRasterSourceDescriptor staged)
                {
                    stagedSources ??= [];
                    stagedSources.Add((key, staged));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // The executor's canonical reader shapes invalid-descriptor errors.
            }
        }

        if (stagedSources is null)
        {
            return new StagedRasterHydration(job, null, null);
        }

        var staging = _stagingOptions?.CurrentValue;
        if (_outputStore is null || _workerOptions is null || staging is not { Enabled: true })
        {
            return new StagedRasterHydration(
                job,
                null,
                "Staged raster inputs require enabled output staging and the matching output store on the GDAL worker.");
        }

        var options = _workerOptions.CurrentValue;
        var workspace = GdalScratch.CreateWorkspace(options.ScratchRoot, job.OperationId + "-staged-inputs");
        try
        {
            var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal);
            for (var index = 0; index < stagedSources.Count; index++)
            {
                var (parameterKey, staged) = stagedSources[index];
                var validation = RasterSourceDescriptorValidator.Validate(
                    staged,
                    cancellationToken: cancellationToken);
                if (!validation.IsValid)
                {
                    var failure = validation.Errors[0];
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input is invalid ({failure.Code}): {failure.Message}");
                }

                if (staged.Provider != _outputStore.Provider
                    || !string.Equals(staged.StoreReference, _outputStore.StoreReference, StringComparison.Ordinal))
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' targets a different output store.");
                }

                if (staged.Content.SizeBytes > options.MaxStagedArtifactBytes)
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' size exceeds configured MaxStagedArtifactBytes={options.MaxStagedArtifactBytes}.");
                }

                if (!await _outputStore.TryAcquireReadLeaseAsync(
                        staged.ObjectKey,
                        staging.ReadLeaseDuration,
                        cancellationToken).ConfigureAwait(false))
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' is unavailable for a protected read.");
                }

                await using var source = await _outputStore.OpenReadAsync(staged.ObjectKey, cancellationToken)
                    .ConfigureAwait(false);
                if (source is null)
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' is unavailable in the configured output store.");
                }

                var fileName = $"source-{index}.tif";
                var outputPath = Path.Join(workspace, fileName);
                var copied = 0L;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(
                    outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        copied = checked(copied + read);
                        if (copied > staged.Content.SizeBytes || copied > options.MaxStagedArtifactBytes)
                        {
                            return new StagedRasterHydration(
                                job,
                                workspace,
                                $"Staged raster input '{staged.ArtifactReference}' exceeded its declared size while materializing.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (copied != staged.Content.SizeBytes)
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' did not match its declared size.");
                }

                var expected = staged.Content.Checksum;
                if (expected is null
                    || !string.Equals(expected.Algorithm, "sha256", StringComparison.Ordinal)
                    || !string.Equals(
                        expected.Value,
                        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                        StringComparison.Ordinal))
                {
                    return new StagedRasterHydration(
                        job,
                        workspace,
                        $"Staged raster input '{staged.ArtifactReference}' failed content-integrity verification.");
                }

                var inputName = parameterKey[GdalWorkerParameterKeys.StepRasterSourcePrefix.Length..];
                parameters[GdalWorkerParameterKeys.HydratedStagedSourcePrefix + inputName] = outputPath;
            }

            return new StagedRasterHydration(
                job with { Spec = job.Spec with { Parameters = parameters } },
                workspace,
                null);
        }
        catch
        {
            GdalScratch.TryCleanup(workspace, _logger);
            throw;
        }
    }

    private sealed record StagedRasterHydration(
        ExecutionJobRecord Job,
        string? Workspace,
        string? Failure);

    private static partial class Log
    {
        [LoggerMessage(9230, LogLevel.Warning,
            "GDAL dispatch executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);
    }
}
