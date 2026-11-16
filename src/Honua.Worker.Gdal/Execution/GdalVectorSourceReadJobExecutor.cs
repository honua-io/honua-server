// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class GdalVectorSourceReadJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalVectorSourceReadJobExecutor> logger) : IProcessExecutor
{
    public const string HandledProcessId = "source.ogr";

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

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

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

        GdalJobInputReader.TryGetInput(parameters, "layerName", out var layerName);
        if (!string.IsNullOrEmpty(layerName)
            && (string.IsNullOrWhiteSpace(layerName) || layerName.Length > 1024
                || layerName.StartsWith('-') || layerName.Any(char.IsControl)))
        {
            return JobExecutionResult.Failed("Invalid source inputs: layerName must be a nonempty layer name, not a command option.");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            string ogrSourcePath;
            if (IsZipArchive(sourceBytes))
            {
                var extracted = await ExtractZipSourceAsync(
                    sourceBytes, workspace, sourceFormat, opts, cancellationToken).ConfigureAwait(false);
                if (extracted.Failure is not null)
                {
                    Log.InvalidInputs(logger, job.OperationId, extracted.Failure);
                    return JobExecutionResult.Failed($"Invalid source inputs: {extracted.Failure}");
                }

                ogrSourcePath = extracted.DatasetPath;
                if (ogrSourcePath.Length == 0)
                {
                    return JobExecutionResult.Failed(
                        "Source ZIP archive did not contain a recognizable OGR dataset (.shp/.gdb/.gml/...).");
                }
            }
            else
            {
                var extension = SourceExtensions.TryGetValue(sourceFormat, out var ext) ? ext : ".dat";
                // Second segment is built from extension, which is always a fixed
                // literal drawn from the SourceExtensions allowlist above (or the
                // ".dat" fallback), never user-supplied, so it can never be rooted and
                // silently discard workspace.
                ogrSourcePath = Path.Join(workspace, "input" + extension);
                await File.WriteAllBytesAsync(ogrSourcePath, sourceBytes, cancellationToken).ConfigureAwait(false);
            }

            // Second segment is a fixed relative literal filename, so it can never be
            // rooted and silently discard workspace.
            var outputPath = Path.Join(workspace, "output.geojson");

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running ogr2ogr source canonicalization", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-f",
                "GeoJSON",
                outputPath,
                ogrSourcePath,
            };

            if (!string.IsNullOrEmpty(layerName))
            {
                // One argv token selects exactly one source layer, including names with spaces.
                args.Add(layerName);
            }

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

            var bounded = await GdalArtifactPublisher.ReadBoundedPayloadAsync(
                outputPath, opts.MaxArtifactBytes, cancellationToken).ConfigureAwait(false);
            if (bounded.ExceededLimit)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, bounded.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Source FeatureCollection size {bounded.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (bounded.Payload is not { Length: > 0 } outputBytes)
            {
                return JobExecutionResult.Failed("ogr2ogr produced an empty FeatureCollection.");
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

    private readonly record struct ZipExtraction(string DatasetPath, string? Failure);

    private static async Task<ZipExtraction> ExtractZipSourceAsync(
        byte[] zipBytes,
        string workspace,
        string sourceFormat,
        GdalWorkerOptions opts,
        CancellationToken cancellationToken)
    {
        // Already cancelled by the time unpack starts: do not create directories
        // or write entry bytes. The caller lets this propagate as cancellation.
        cancellationToken.ThrowIfCancellationRequested();

        var maxEntry = opts.MaxArchiveEntryBytes > 0
            ? opts.MaxArchiveEntryBytes
            : FileSizeConstants.FiveHundredMB;
        var maxTotal = opts.MaxArchiveExtractedBytes > 0
            ? opts.MaxArchiveExtractedBytes
            : FileSizeConstants.OneGB;
        var maxRatio = opts.MaxArchiveCompressionRatio > 1d
            ? opts.MaxArchiveCompressionRatio
            : 200d;
        var maxEntries = opts.MaxArchiveEntries > 0
            ? opts.MaxArchiveEntries
            : 100_000;

        // Second segment is a fixed relative literal, so it can never be rooted and
        // silently discard workspace. The directory is created only after every
        // entry name and declared budget has been accepted.
        var extractRoot = Path.Join(workspace, "src");
        var fullExtractRoot = Path.GetFullPath(extractRoot) + Path.DirectorySeparatorChar;

        using var stream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > maxEntries)
        {
            return new ZipExtraction(
                string.Empty,
                "Archive entry count " + archive.Entries.Count.ToString(CultureInfo.InvariantCulture)
                + " exceeds maximum " + maxEntries.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var declaredTotal = 0L;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declaredFailure = ValidateDeclaredEntry(
                entry, extractRoot, fullExtractRoot, maxEntry, maxTotal, maxRatio, ref declaredTotal);
            if (declaredFailure is not null)
            {
                return new ZipExtraction(string.Empty, declaredFailure);
            }
        }

        Directory.CreateDirectory(extractRoot);
        var buffer = new byte[64 * 1024];
        var writtenTotal = 0L;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInvalidEntryName(entry.FullName))
            {
                return new ZipExtraction(string.Empty, "Archive contains invalid entry name.");
            }

            // FullName is attacker-controlled. Resolve and reject it here, in the
            // method that writes, so a path that escapes the extraction root is
            // never passed to a filesystem call.
            string destination;
            try
            {
                destination = Path.GetFullPath(Path.Join(extractRoot, entry.FullName));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return new ZipExtraction(string.Empty, "Archive contains path traversal.");
            }

            if (!destination.StartsWith(fullExtractRoot, StringComparison.Ordinal))
            {
                return new ZipExtraction(string.Empty, "Archive contains path traversal.");
            }

            if (IsDirectoryEntry(entry))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try
            {
                await using var entryStream = entry.Open();
                await using var output = new FileStream(
                    destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                var entryWritten = 0L;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await entryStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    entryWritten += read;
                    if (entryWritten > maxEntry)
                    {
                        return new ZipExtraction(string.Empty, UncompressedSizeMessage(entry.FullName, maxEntry));
                    }

                    if (read > maxTotal - writtenTotal)
                    {
                        return new ZipExtraction(string.Empty, TotalUncompressedMessage(maxTotal));
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    writtenTotal += read;
                }
            }
            catch (InvalidDataException)
            {
                return new ZipExtraction(string.Empty, "Archive entry could not be read.");
            }
        }

        return new ZipExtraction(DiscoverDataset(extractRoot, sourceFormat), null);
    }

    private static string? ValidateDeclaredEntry(
        ZipArchiveEntry entry,
        string extractRoot,
        string fullExtractRoot,
        long maxEntry,
        long maxTotal,
        double maxRatio,
        ref long declaredTotal)
    {
        if (!TryResolveEntryDestination(entry.FullName, extractRoot, fullExtractRoot, out _, out var nameFailure))
        {
            return nameFailure;
        }

        if (entry.Length < 0 || entry.CompressedLength < 0)
        {
            return "Archive entry '" + entry.FullName + "' has an invalid size.";
        }

        if (entry.Length > maxEntry)
        {
            return UncompressedSizeMessage(entry.FullName, maxEntry);
        }

        if (entry.Length > maxTotal - declaredTotal)
        {
            return TotalUncompressedMessage(maxTotal);
        }

        if (entry.Length > 0)
        {
            if (entry.CompressedLength <= 0)
            {
                return "Archive entry '" + entry.FullName + "' has invalid compressed size.";
            }

            var ratio = (double)entry.Length / entry.CompressedLength;
            if (ratio > maxRatio)
            {
                return "Archive entry '" + entry.FullName + "' exceeds maximum compression ratio ("
                    + maxRatio.ToString(CultureInfo.InvariantCulture) + ").";
            }
        }

        declaredTotal += entry.Length;
        return null;
    }

    private static string UncompressedSizeMessage(string fullName, long maxEntry)
        => "Archive entry '" + fullName + "' exceeds maximum uncompressed size ("
            + maxEntry.ToString(CultureInfo.InvariantCulture) + " bytes).";

    private static string TotalUncompressedMessage(long maxTotal)
        => "Archive extraction exceeds maximum total uncompressed size ("
            + maxTotal.ToString(CultureInfo.InvariantCulture) + " bytes).";

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => string.IsNullOrEmpty(entry.Name)
            || entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\');

    private static bool TryResolveEntryDestination(
        string fullName,
        string extractRoot,
        string fullExtractRoot,
        out string destination,
        out string? failure)
    {
        destination = string.Empty;
        if (IsInvalidEntryName(fullName))
        {
            failure = "Archive contains invalid entry name.";
            return false;
        }

        try
        {
            // fullName is attacker-influenced and can be rooted, which would make
            // Path.Join discard extractRoot. GetFullPath plus the prefix check is
            // the containment guard; invalid names are rejected above first.
            destination = Path.GetFullPath(Path.Join(extractRoot, fullName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            failure = "Archive contains path traversal.";
            return false;
        }

        if (!destination.StartsWith(fullExtractRoot, StringComparison.Ordinal))
        {
            destination = string.Empty;
            failure = "Archive contains path traversal.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsInvalidEntryName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || fullName[0] is '/' or '\\' || fullName.Contains(':'))
        {
            return true;
        }

        var parts = fullName.Split(['/', '\\']);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                if (i != parts.Length - 1)
                {
                    return true;
                }

                continue;
            }

            if (part is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    private static string DiscoverDataset(string extractRoot, string sourceFormat)
    {
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
