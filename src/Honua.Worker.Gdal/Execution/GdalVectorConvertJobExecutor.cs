// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the <c>gdal.ogr2ogr</c> process:
/// vector format conversion backed by the GDAL/OGR <c>ogr2ogr</c> CLI.
///
/// This is the heavyweight counterpart to the lean managed vector executors. It
/// runs only inside the GDAL worker image (it overrides
/// <see cref="AcceptedRuntimeProfiles"/> to <c>{ "native" }</c>, so the substrate
/// claim fence keeps the lean managed worker from ever claiming it). It reads a
/// base64 source dataset and a target OGR driver from the durable spec, runs the
/// conversion in an isolated scratch workspace, and publishes the converted bytes
/// as a canonical data-URI artifact through the shared execution context.
/// </summary>
internal sealed partial class GdalVectorConvertJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalVectorConvertJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "gdal.ogr2ogr";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    private static readonly FrozenDictionary<string, (string Extension, string ContentType)> SupportedFormats =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["GeoJSON"] = (".geojson", "application/geo+json"),
            ["GPKG"] = (".gpkg", "application/geopackage+sqlite3"),
            ["CSV"] = (".csv", "text/csv"),
            ["FlatGeobuf"] = (".fgb", "application/octet-stream"),
            ["ESRI Shapefile"] = (".shp", "application/octet-stream"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
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
        await context.ReportProgressAsync(5, "Parsing conversion inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "sourceFormat", out var sourceFormat))
        {
            sourceFormat = "GeoJSON";
        }

        if (!GdalJobInputReader.TryGetInput(parameters, "targetFormat", out var targetFormat))
        {
            return JobExecutionResult.Failed("Invalid conversion inputs: missing required input 'targetFormat'.");
        }

        if (!SupportedFormats.TryGetValue(targetFormat, out var targetMeta))
        {
            var supported = string.Join(", ", SupportedFormats.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return JobExecutionResult.Failed(
                $"Target format '{targetFormat}' is not supported by the {HandledProcessId} executor. " +
                $"Supported: {supported}.");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var inputError))
        {
            Log.InvalidInputs(logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid conversion inputs: {inputError}");
        }

        var sourceExtension = SupportedFormats.TryGetValue(sourceFormat, out var sourceMeta)
            ? sourceMeta.Extension
            : ".dat";

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input" + sourceExtension);
            var outputName = "output" + targetMeta.Extension;
            var outputPath = Path.Combine(workspace, outputName);

            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running ogr2ogr conversion", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-f",
                targetFormat,
                outputPath,
                inputPath,
            };

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("ogr2ogr", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"ogr2ogr timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"ogr2ogr exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed(
                    "ogr2ogr reported success but produced no output file; verify the source dataset and target format.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding conversion artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("ogr2ogr produced an empty output dataset.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Converted artifact size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(targetMeta.ContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Conversion completed", cancellationToken).ConfigureAwait(false);

            Log.ConversionCompleted(logger, job.OperationId, targetFormat, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9210, LogLevel.Warning,
            "GDAL vector convert executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9211, LogLevel.Warning,
            "GDAL vector convert executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9212, LogLevel.Error,
            "GDAL vector convert executor failed job {OperationId}: ogr2ogr exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9213, LogLevel.Error,
            "GDAL vector convert executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9214, LogLevel.Warning,
            "GDAL vector convert executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9215, LogLevel.Information,
            "GDAL vector convert executor completed job {OperationId}: format={Format}, bytes={Bytes}")]
        public static partial void ConversionCompleted(ILogger logger, string operationId, string format, long bytes);
    }
}
