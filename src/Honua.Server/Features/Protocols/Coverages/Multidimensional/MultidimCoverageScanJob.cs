// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;

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
    /// Decodes the worker's <c>data:</c> artifact and maps the <c>gdalmdiminfo</c>
    /// JSON into canonical coverage metadata. Returns null when the artifact is
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
            return GdalMultidimensionalMetadataMapper.Map(json, format, variables);
        }
        catch (InvalidOperationException)
        {
            // MultidimensionalCoverageUnsupportedLayoutException et al. — the worker
            // produced output the mapper could not interpret. Treat as no metadata.
            return null;
        }
    }

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
