// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.IO.Compression;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the <c>source.ogr</c> process: a
/// GDAL/OGR-backed import reader that canonicalizes a broad-format source dataset
/// into the standard GeoJSON FeatureCollection artifact every workflow DAG starts
/// from.
///
/// This is the heavyweight counterpart to the lean managed <c>source.geojson</c> /
/// <c>source.csv</c> readers, which are hand-rolled NetTopologySuite parsers covering
/// only GeoJSON and CSV. <c>source.ogr</c> reaches the FULL OGR driver universe the
/// managed readers cannot — native File Geodatabase (OpenFileGDB), GML / GML
/// application schemas, KML, MapInfo TAB, ESRI Shapefile, GeoPackage, FlatGeobuf, and
/// (where the worker image's drivers are compiled in) database-spatial sources — and
/// converts them to GeoJSON via <c>ogr2ogr -f GeoJSON</c> in an isolated scratch
/// workspace.
///
/// It runs only inside the GDAL worker image (it overrides
/// <see cref="AcceptedRuntimeProfiles"/> to <c>{ "native" }</c>, so the substrate
/// claim fence keeps the lean managed worker from ever claiming it). The source
/// dataset arrives as a base64 step input; some formats (Shapefile, FileGDB) are
/// multi-file directories shipped as a base64 ZIP, so the executor unzips a
/// <c>.zip</c> payload into the workspace and points OGR at the extracted dataset via
/// GDAL's <c>/vsizip/</c>-free directory open. The canonicalized FeatureCollection is
/// published as a <c>data:application/geo+json;base64,</c> artifact, matching the
/// managed sources so downstream nodes are agnostic to which reader produced it.
/// </summary>
internal sealed partial class GdalVectorSourceReadJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalVectorSourceReadJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "source.ogr";

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    private const string GeoJsonContentType = "application/geo+json";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Source-format hint -> input file extension. The hint selects the on-disk
    // extension OGR uses to choose the driver; OGR is otherwise content-sniffing.
    // ZIP-packaged multi-file datasets (Shapefile, FileGDB) are detected by the
    // ".zip" magic on the decoded bytes and extracted, regardless of the hint.
    private static readonly FrozenDictionary<string, string> SourceExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GeoJSON"] = ".geojson",
            ["GML"] = ".gml",
            ["KML"] = ".kml",
            ["GPKG"] = ".gpkg",
            ["FlatGeobuf"] = ".fgb",
            ["MapInfo File"] = ".tab",
            ["CSV"] = ".csv",
            ["ESRI Shapefile"] = ".shp",
            ["OpenFileGDB"] = ".gdb",
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
        await context.ReportProgressAsync(5, "Parsing source inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "sourceFormat", out var sourceFormat)
            || string.IsNullOrWhiteSpace(sourceFormat))
        {
            sourceFormat = "GeoJSON";
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var inputError))
        {
            Log.InvalidInputs(logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid source inputs: {inputError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            string ogrSourcePath;
            if (IsZipArchive(sourceBytes))
            {
                ogrSourcePath = ExtractZipSource(sourceBytes, workspace, sourceFormat);
                if (ogrSourcePath.Length == 0)
                {
                    return JobExecutionResult.Failed(
                        "Source ZIP archive did not contain a recognizable OGR dataset (.shp/.gdb/.gml/...).");
                }
            }
            else
            {
                var extension = SourceExtensions.TryGetValue(sourceFormat, out var ext) ? ext : ".dat";
                ogrSourcePath = Path.Combine(workspace, "input" + extension);
                await File.WriteAllBytesAsync(ogrSourcePath, sourceBytes, cancellationToken).ConfigureAwait(false);
            }

            var outputPath = Path.Combine(workspace, "output.geojson");

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running ogr2ogr source canonicalization", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-f",
                "GeoJSON",
                outputPath,
                ogrSourcePath,
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
                    "ogr2ogr reported success but produced no FeatureCollection; verify the source dataset and format hint.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding source artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("ogr2ogr produced an empty FeatureCollection.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Source FeatureCollection size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoJsonContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Source read completed", cancellationToken).ConfigureAwait(false);

            Log.SourceReadCompleted(logger, job.OperationId, sourceFormat, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    // ZIP local-file-header magic: 'P' 'K' 0x03 0x04.
    private static bool IsZipArchive(byte[] bytes)
        => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    /// <summary>
    /// Extracts a ZIP-packaged multi-file dataset into a sandboxed sub-directory of
    /// the scratch workspace and returns the OGR-openable dataset path (the
    /// containing directory for a <c>.gdb</c> FileGDB, or the single <c>.shp</c>/
    /// recognized vector file otherwise). Entry paths are validated to stay within
    /// the extraction root (no zip-slip). Returns an empty string when no
    /// recognizable dataset is found.
    /// </summary>
    private static string ExtractZipSource(byte[] zipBytes, string workspace, string sourceFormat)
    {
        var extractRoot = Path.Combine(workspace, "src");
        Directory.CreateDirectory(extractRoot);
        var fullExtractRoot = Path.GetFullPath(extractRoot) + Path.DirectorySeparatorChar;

        using (var stream = new MemoryStream(zipBytes, writable: false))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                // Directory entries have empty names.
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                var destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.Ordinal))
                {
                    // Zip-slip: entry escapes the extraction root — skip it.
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }

        // A FileGDB is a directory ending in .gdb; OGR opens the directory.
        var gdb = Directory
            .EnumerateDirectories(extractRoot, "*.gdb", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (gdb is not null)
        {
            return gdb;
        }

        // Otherwise find the first recognized single-file vector dataset. Prefer the
        // hinted extension, then fall back to the common single-file vector formats.
        var preferred = SourceExtensions.TryGetValue(sourceFormat, out var ext) ? ext : null;
        var candidates = new[] { preferred, ".shp", ".gml", ".kml", ".gpkg", ".fgb", ".tab", ".geojson", ".json" }
            .Where(e => e is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select(candidate => Directory
                .EnumerateFiles(extractRoot, "*" + candidate, SearchOption.AllDirectories)
                .FirstOrDefault())
            .FirstOrDefault(match => match is not null) ?? string.Empty;
    }

    private static partial class Log
    {
        [LoggerMessage(9250, LogLevel.Warning,
            "GDAL source read executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9251, LogLevel.Warning,
            "GDAL source read executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9252, LogLevel.Error,
            "GDAL source read executor failed job {OperationId}: ogr2ogr exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9253, LogLevel.Error,
            "GDAL source read executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9254, LogLevel.Warning,
            "GDAL source read executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9255, LogLevel.Information,
            "GDAL source read executor completed job {OperationId}: sourceFormat={SourceFormat}, bytes={Bytes}")]
        public static partial void SourceReadCompleted(ILogger logger, string operationId, string sourceFormat, long bytes);
    }
}
