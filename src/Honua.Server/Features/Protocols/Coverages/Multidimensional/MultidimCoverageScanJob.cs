// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Submit-side helper for the ADR-0039 Path B multidimensional-coverage metadata
/// scan. Projects a coverage registration onto a durable
/// <see cref="ExecutionJobKind.Geoprocessing"/> job that the GDAL native worker
/// claims (process id <c>coverage.multidim.metadata</c>), then maps the worker's
/// <c>gdalmdiminfo</c> artifact back into canonical coverage metadata.
/// </summary>
internal static class MultidimCoverageScanJob
{
    /// <summary>
    /// Canonical process id. MUST match
    /// <c>GdalMultidimCoverageMetadataJobExecutor.HandledProcessId</c> in the GDAL
    /// worker (string contract between submit and execute).
    /// </summary>
    public const string ProcessId = "coverage.multidim.metadata";

    /// <summary>
    /// Spec parameter carrying the coverage registration id so the status endpoint
    /// can resolve which registration a completed scan belongs to. Not read by the
    /// worker.
    /// </summary>
    public const string RegistrationIdParam = "honua.coverage.registration_id";

    private const string JobContentType = "application/json";

    /// <summary>
    /// Builds the execution-job spec for a coverage metadata scan.
    /// </summary>
    public static ExecutionJobSpec BuildSpec(MultidimensionalCoverageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = ProcessId,
            [StepInput("provider")] = registration.Provider.ToString(),
            [StepInput("bucket")] = registration.Bucket,
            [StepInput("objectKey")] = registration.ObjectKey,
            [RegistrationIdParam] = registration.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = LocalBatchComputeBackend.BackendId,
            WorkloadName = $"coverage-multidim-scan:{registration.Id}",
            // Native profile routes the job to the GDAL worker (NetCDF/HDF5 drivers)
            // via the claim fence and away from the lean managed dispatcher.
            RuntimeProfile = RuntimeProfiles.Native,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Creates and enqueues the scan job, returning its stable job id.
    /// </summary>
    public static async Task<string> SubmitAsync(
        IExecutionJobStore jobStore,
        IJobQueue jobQueue,
        MultidimensionalCoverageRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(jobQueue);
        ArgumentNullException.ThrowIfNull(registration);

        var jobId = $"covscan-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var record = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Spec = BuildSpec(registration),
        };

        var created = await jobStore.TryCreateAsync(record, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException($"Failed to create coverage scan job '{jobId}'.");
        }

        await jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return jobId;
    }

    /// <summary>
    /// Returns true when the job is a coverage metadata scan (so the status
    /// endpoint does not leak unrelated job ids).
    /// </summary>
    public static bool IsScanJob(ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Spec.Parameters.TryGetValue(
                   ExecutionJobParameterKeys.GeoprocessingProcessDefinitions, out var process) &&
               string.Equals(process, ProcessId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the coverage registration id stamped on a scan job, if present.
    /// </summary>
    public static bool TryGetRegistrationId(ExecutionJobRecord job, out long registrationId)
    {
        ArgumentNullException.ThrowIfNull(job);
        registrationId = 0;
        return job.Spec.Parameters.TryGetValue(RegistrationIdParam, out var raw) &&
               long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out registrationId);
    }

    /// <summary>
    /// Decodes the worker's <c>data:</c> artifact (a <c>{ "mdiminfo", "info" }</c>
    /// envelope), maps the <c>gdalmdiminfo</c> structure into canonical coverage
    /// metadata, and enriches it with extent/temporal bounds from the
    /// <c>gdalinfo</c> document when present. Returns null when the artifact is
    /// missing or unparseable.
    /// </summary>
    public static MultidimensionalCoverageMetadata? TryMapArtifact(
        string? artifactReference,
        MultidimensionalCoverageFormat format,
        IReadOnlyList<string> variables)
    {
        if (!TryDecodeDataUri(artifactReference, out var json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mdiminfo", out var mdim))
            {
                return null;
            }

            var metadata = GdalMultidimensionalMetadataMapper.Map(mdim.GetRawText(), format, variables);

            if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                metadata = GdalInfoCoverageEnricher.Enrich(metadata, info.GetRawText());
            }

            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // MultidimensionalCoverageUnsupportedLayoutException et al. — the worker
            // produced output the mapper could not interpret. Treat as no metadata.
            return null;
        }
    }

    /// <summary>
    /// Reads the derived Zarr store root the worker wrote (and reported in the
    /// artifact envelope) when the NetCDF→Zarr convert succeeded.
    /// </summary>
    public static bool TryGetZarrRootPath(string? artifactReference, out string rootPath)
    {
        rootPath = string.Empty;
        if (!TryDecodeDataUri(artifactReference, out var json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("zarr", out var zarr) &&
                zarr.ValueKind == JsonValueKind.Object &&
                zarr.TryGetProperty("rootPath", out var rp) &&
                rp.ValueKind == JsonValueKind.String)
            {
                var value = rp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    rootPath = value;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return false;
    }

    /// <summary>
    /// Registers the worker's derived Zarr store as a normal Zarr coverage for the
    /// same layer so OGC Coverages serves pixel slices through the existing Zarr
    /// reader. Idempotent across status polls; the metadata scan is best-effort
    /// (a later Zarr refresh completes it if the cloud read is unavailable here).
    /// </summary>
    public static async Task RegisterDerivedZarrAsync(
        IZarrStore zarrStore,
        IZarrMetadataReader zarrMetadataReader,
        IEnumerable<ICloudRangeReader> rangeReaders,
        MultidimensionalCoverageRegistration registration,
        string zarrRootPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zarrStore);
        ArgumentNullException.ThrowIfNull(registration);

        var existing = await zarrStore.ListByLayerAsync(registration.LayerId, cancellationToken).ConfigureAwait(false);
        var alreadyRegistered = existing.Any(candidate =>
            candidate.Provider == registration.Provider &&
            string.Equals(candidate.Bucket, registration.Bucket, StringComparison.Ordinal) &&
            string.Equals(candidate.RootPath, zarrRootPath, StringComparison.Ordinal));
        if (alreadyRegistered)
        {
            return; // Already registered by an earlier poll.
        }

        var zarr = await zarrStore.RegisterAsync(
            new ZarrRegistrationRequest
            {
                LayerId = registration.LayerId,
                Name = $"{registration.Name} (Zarr)",
                Description = $"Derived from multidimensional coverage {registration.Id}.",
                Provider = registration.Provider,
                Bucket = registration.Bucket,
                RootPath = zarrRootPath,
            },
            cancellationToken).ConfigureAwait(false);

        var rangeReader = rangeReaders?.FirstOrDefault(r => r.Provider == registration.Provider);
        if (rangeReader is null)
        {
            return;
        }

        try
        {
            var metadata = await zarrMetadataReader
                .ReadMetadataAsync(rangeReader, registration.Bucket, zarrRootPath, cancellationToken)
                .ConfigureAwait(false);
            if (registration.Metadata is { } sourceMetadata)
            {
                metadata = EnrichDerivedZarrMetadata(metadata, sourceMetadata);
            }
            await zarrStore.UpdateMetadataAsync(zarr.Id, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Registration stands; the derived store becomes servable once its
            // metadata is scanned (e.g. via the Zarr admin refresh).
            MultidimensionalCoverageLog.DerivedZarrScanDeferred(logger, zarr.Id, ex.Message);
        }
    }

    /// <summary>
    /// Carries the authoritative CF/GDAL metadata discovered from the source into
    /// the worker-created Zarr registration. The conversion preserves array data
    /// and row order but does not guarantee that its root attributes contain the
    /// server's private georeferencing manifest.
    /// </summary>
    internal static ZarrStoreMetadata EnrichDerivedZarrMetadata(
        ZarrStoreMetadata zarrMetadata,
        MultidimensionalCoverageMetadata sourceMetadata)
    {
        ArgumentNullException.ThrowIfNull(zarrMetadata);
        ArgumentNullException.ThrowIfNull(sourceMetadata);

        var primary = zarrMetadata.Arrays.FirstOrDefault(array =>
            sourceMetadata.Variables.Any(variable =>
                string.Equals(variable.Name, array.Name, StringComparison.OrdinalIgnoreCase)))
            ?? zarrMetadata.Arrays.FirstOrDefault();

        var xDim = zarrMetadata.SpatialXDimension ?? FindDimension(primary, IsXDimension);
        var yDim = zarrMetadata.SpatialYDimension ?? FindDimension(primary, IsYDimension);
        var tDim = zarrMetadata.TemporalDimension ?? FindDimension(primary, IsTimeDimension);

        var axes = zarrMetadata.Axes;
        if (sourceMetadata.Vertical is { } vertical && primary is not null)
        {
            var axisName = primary.DimensionNames.FirstOrDefault(name =>
                !IsSame(name, xDim) && !IsSame(name, yDim) && !IsSame(name, tDim));
            if (axisName is not null && axes.All(axis => !IsSame(axis.Name, axisName)))
            {
                axes =
                [
                    ..axes,
                    new ZarrAxis(
                        axisName,
                        primary.Shape[Array.IndexOf(primary.DimensionNames, axisName)],
                        Coordinates: null,
                        vertical.Min,
                        vertical.Max,
                        vertical.Units,
                        Positive: null),
                ];
            }
        }

        var hasSourceGrid = sourceMetadata.Extent is not null ||
            sourceMetadata.Resolution is { X: not 0, Y: not 0 };

        return zarrMetadata with
        {
            Srid = sourceMetadata.Srid > 0 ? sourceMetadata.Srid : zarrMetadata.Srid,
            Extent = sourceMetadata.Extent ?? zarrMetadata.Extent,
            SpatialXDimension = xDim,
            SpatialYDimension = yDim,
            TemporalDimension = tDim,
            Temporal = sourceMetadata.Temporal ?? zarrMetadata.Temporal,
            Axes = axes,
            YAxisAscending = hasSourceGrid ? sourceMetadata.YAxisAscending : zarrMetadata.YAxisAscending,
        };
    }

    private static string? FindDimension(ZarrArrayMetadata? array, Func<string, bool> predicate)
        => array?.DimensionNames.FirstOrDefault(predicate);

    private static bool IsXDimension(string name)
        => name.ToLowerInvariant() is "x" or "lon" or "longitude";

    private static bool IsYDimension(string name)
        => name.ToLowerInvariant() is "y" or "lat" or "latitude";

    private static bool IsTimeDimension(string name)
        => name.ToLowerInvariant() is "t" or "time" or "datetime";

    private static bool IsSame(string? left, string? right)
        => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string StepInput(string name)
        => $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.{name}";

    private static bool TryDecodeDataUri(string? artifactReference, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(artifactReference))
        {
            return false;
        }

        // Format produced by GdalDataUri.Build: data:<content-type>;base64,<payload>
        const string marker = ";base64,";
        var markerIndex = artifactReference.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0 || !artifactReference.StartsWith("data:" + JobContentType, StringComparison.Ordinal))
        {
            return false;
        }

        var base64 = artifactReference[(markerIndex + marker.Length)..];
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return !string.IsNullOrWhiteSpace(json);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
