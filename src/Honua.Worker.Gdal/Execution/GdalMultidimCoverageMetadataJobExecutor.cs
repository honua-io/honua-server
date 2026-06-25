// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the internal
/// <c>coverage.multidim.metadata</c> job: extracts the structure of a
/// cloud-hosted NetCDF4/HDF5 multidimensional coverage with
/// <c>gdalmdiminfo</c> and publishes the raw JSON as the job artifact.
/// </summary>
/// <remarks>
/// This is the Path B reader recorded in ADR-0039. The worker reads the source
/// in place through GDAL's VSI layer (range reads, no whole-file download) and
/// returns the verbatim <c>gdalmdiminfo</c> document; the server maps it to the
/// canonical coverage metadata via
/// <c>GdalMultidimensionalMetadataMapper</c>, keeping all native format handling
/// out of the AOT-published server. It is an internal admin job (driven by the
/// coverage refresh endpoint), not an advertised geoprocessing process, so it
/// is intentionally absent from the public process catalog.
/// </remarks>
internal sealed partial class GdalMultidimCoverageMetadataJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalMultidimCoverageMetadataJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "coverage.multidim.metadata";

    private const string MetadataContentType = "application/json";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <inheritdoc />
    /// <summary>
    /// The single process id this executor handles, surfaced through
    /// <see cref="IProcessExecutor"/> so the GDAL dispatcher auto-registers it (#2122).
    /// </summary>
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GdalJobInputReader.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing coverage inputs", cancellationToken).ConfigureAwait(false);

        if (!GdalJobInputReader.TryGetInput(parameters, "provider", out var providerRaw) ||
            !Enum.TryParse<CloudStorageProvider>(providerRaw, ignoreCase: true, out var provider))
        {
            return JobExecutionResult.Failed(
                "Invalid coverage inputs: 'provider' must be one of Local, AwsS3, AzureBlob.");
        }

        if (!GdalJobInputReader.TryGetInput(parameters, "bucket", out var bucket) || string.IsNullOrWhiteSpace(bucket))
        {
            return JobExecutionResult.Failed("Invalid coverage inputs: missing required input 'bucket'.");
        }

        if (!GdalJobInputReader.TryGetInput(parameters, "objectKey", out var objectKey) || string.IsNullOrWhiteSpace(objectKey))
        {
            return JobExecutionResult.Failed("Invalid coverage inputs: missing required input 'objectKey'.");
        }

        string vsiPath;
        try
        {
            vsiPath = GdalVsiPath.Build(provider, bucket, objectKey);
        }
        catch (NotSupportedException ex)
        {
            return JobExecutionResult.Failed($"Invalid coverage inputs: {ex.Message}");
        }

        var opts = options.CurrentValue;
        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalmdiminfo", cancellationToken).ConfigureAwait(false);

            var args = new List<string> { vsiPath };

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdalmdiminfo", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdalmdiminfo timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalmdiminfo exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return JobExecutionResult.Failed(
                    "gdalmdiminfo reported success but produced no output; verify the source is a readable multidimensional dataset.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(70, "Reading coverage extent", cancellationToken).ConfigureAwait(false);

            // Best-effort enrichment: gdalmdiminfo carries structure but no coordinate
            // values, so a classic gdalinfo pass supplies the spatial extent, cell
            // resolution, and temporal/vertical bounds. A failure here is non-fatal —
            // the server still materializes structural metadata from gdalmdiminfo.
            string? infoJson = null;
            var infoResult = await RunInfoAsync(runner, vsiPath, workspace, linked.Token).ConfigureAwait(false);
            if (infoResult is { Succeeded: true } && !string.IsNullOrWhiteSpace(infoResult.StandardOutput))
            {
                infoJson = infoResult.StandardOutput;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Converting to Zarr", cancellationToken).ConfigureAwait(false);

            // Best-effort convert so pixel slices resolve through the existing Zarr
            // reader (ADR-0039 Path B). Writes a derived Zarr beside the source via
            // GDAL's VSI layer; a failure here leaves metadata serving intact.
            string? zarrRootPath = DeriveZarrRootPath(objectKey);
            var vsiOut = GdalVsiPath.Build(provider, bucket, zarrRootPath);
            var convertResult = await RunConvertAsync(runner, vsiPath, vsiOut, workspace, linked.Token).ConfigureAwait(false);
            if (convertResult is not { Succeeded: true })
            {
                zarrRootPath = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(85, "Publishing coverage metadata", cancellationToken).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(BuildCombinedArtifact(result.StandardOutput, infoJson, zarrRootPath));
            if (payload.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, payload.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Coverage metadata size {payload.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(MetadataContentType, payload);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Coverage metadata completed", cancellationToken).ConfigureAwait(false);

            Log.MetadataCompleted(logger, job.OperationId, provider.ToString(), payload.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static async Task<GdalCommandResult?> RunInfoAsync(
        IGdalCommandRunner runner,
        string vsiPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync("gdalinfo", new List<string> { "-json", vsiPath }, workspace, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<GdalCommandResult?> RunConvertAsync(
        IGdalCommandRunner runner,
        string vsiIn,
        string vsiOut,
        string workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runner
                .RunAsync("gdal_translate", new List<string> { "-of", "Zarr", vsiIn, vsiOut }, workspace, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Derives the Zarr store key beside the source object: the source key with its
    /// final extension replaced by <c>.zarr</c> (e.g. <c>maui/sst.nc</c> -&gt;
    /// <c>maui/sst.zarr</c>).
    /// </summary>
    internal static string DeriveZarrRootPath(string objectKey)
    {
        var trimmed = objectKey.TrimStart('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var lastDot = trimmed.LastIndexOf('.');
        var stem = lastDot > lastSlash ? trimmed[..lastDot] : trimmed;
        return stem + ".zarr";
    }

    private static string BuildCombinedArtifact(string mdimJson, string? infoJson, string? zarrRootPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("mdiminfo");
            using (var mdim = JsonDocument.Parse(mdimJson))
            {
                mdim.RootElement.WriteTo(writer);
            }

            if (!string.IsNullOrWhiteSpace(infoJson))
            {
                try
                {
                    using var info = JsonDocument.Parse(infoJson);
                    writer.WritePropertyName("info");
                    info.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    // gdalinfo emitted non-JSON; skip enrichment payload.
                }
            }

            if (!string.IsNullOrWhiteSpace(zarrRootPath))
            {
                writer.WriteStartObject("zarr");
                writer.WriteString("rootPath", zarrRootPath);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static partial class Log
    {
        [LoggerMessage(9300, LogLevel.Warning,
            "GDAL multidim coverage executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9301, LogLevel.Error,
            "GDAL multidim coverage executor failed job {OperationId}: gdalmdiminfo exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9302, LogLevel.Error,
            "GDAL multidim coverage executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9303, LogLevel.Warning,
            "GDAL multidim coverage executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9304, LogLevel.Information,
            "GDAL multidim coverage executor completed job {OperationId}: provider={Provider}, bytes={Bytes}")]
        public static partial void MetadataCompleted(ILogger logger, string operationId, string provider, long bytes);
    }
}
