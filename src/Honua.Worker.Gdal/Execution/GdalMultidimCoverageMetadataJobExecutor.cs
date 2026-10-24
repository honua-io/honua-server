// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    /// <summary>
    /// Ceiling for the optional <c>gdalinfo -stats</c> pass. Coordinates are read
    /// first; statistics must not share the conversion timeout, and a stats
    /// timeout keeps the coordinate document already obtained.
    /// </summary>
    private static readonly TimeSpan StatisticsBudget = TimeSpan.FromSeconds(30);

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

            await GdalCommandLog.LogCommandAsync(context, "gdalmdiminfo", args, workspace, cancellationToken).ConfigureAwait(false);

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
            await context.ReportProgressAsync(70, "Converting to Zarr", cancellationToken).ConfigureAwait(false);

            // Classic NetCDF raster views normalize rows to north-up, while
            // gdalmdimtranslate preserves storage order. Read only the Y coordinate
            // endpoints to bind the metadata to the actual multidimensional rows.
            var yAxisAscending = await ReadYAxisAscendingAsync(
                runner, vsiPath, result.StandardOutput, workspace, linked.Token).ConfigureAwait(false);

            // Best-effort convert so pixel slices resolve through the existing Zarr
            // reader (ADR-0039 Path B). This is a multidimensional dataset, so use
            // gdalmdimtranslate rather than the classic-raster gdal_translate CLI.
            // The same derived store is what classic gdalinfo can open inside a
            // container: GDAL's netCDF driver refuses /vsi sources unless Linux
            // userfaultfd is available, and Docker's default seccomp blocks it.
            string? zarrRootPath = DeriveZarrRootPath(objectKey);
            var vsiOut = GdalVsiPath.Build(provider, bucket, zarrRootPath);
            var convertResult = await RunConvertAsync(runner, context, vsiPath, vsiOut, workspace, linked.Token).ConfigureAwait(false);
            if (convertResult is not { Succeeded: true })
            {
                zarrRootPath = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Reading coverage extent", cancellationToken).ConfigureAwait(false);

            // Coordinate enrichment must not open the source with the classic netCDF
            // driver. Prefer gdalinfo on the derived Zarr, including the multidimensional
            // array subdataset the classic API exposes for a 3D+ store. If that is
            // unavailable, copy the bytes to local scratch and read them there — a
            // local NetCDF does not need userfaultfd.
            var (infoJson, enrichmentWarning) = await ReadCoordinatesAsync(
                runner,
                context,
                vsiPath,
                zarrRootPath is null ? null : vsiOut,
                result.StandardOutput,
                workspace,
                cancellationToken,
                linked.Token).ConfigureAwait(false);
            if (enrichmentWarning is not null)
            {
                Log.EnrichmentFailed(logger, job.OperationId, enrichmentWarning);
                await context.AppendLogAsync(
                    new ExecutionLogEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = ExecutionLogLevel.Warning,
                        Message = enrichmentWarning,
                        Phase = "Reading coverage extent",
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(85, "Publishing coverage metadata", cancellationToken).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(BuildCombinedArtifact(result.StandardOutput, infoJson, zarrRootPath, yAxisAscending));
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
            return enrichmentWarning is null
                ? JobExecutionResult.Succeeded()
                : JobExecutionResult.Succeeded([enrichmentWarning]);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static async Task<bool?> ReadYAxisAscendingAsync(
        IGdalCommandRunner runner,
        string vsiPath,
        string mdimJson,
        string workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            using var structure = JsonDocument.Parse(mdimJson);
            if (structure.RootElement.ValueKind != JsonValueKind.Object ||
                !structure.RootElement.TryGetProperty("dimensions", out var dimensions) ||
                dimensions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? coordinate = null;
            foreach (var dimension in dimensions.EnumerateArray())
            {
                if (dimension.ValueKind != JsonValueKind.Object ||
                    !dimension.TryGetProperty("indexing_variable", out var indexing))
                {
                    continue;
                }

                var isYAxis = dimension.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "HORIZONTAL_Y";
                string? candidate = isYAxis && indexing.ValueKind == JsonValueKind.String ? indexing.GetString() : null;
                if (indexing.ValueKind == JsonValueKind.Object)
                {
                    foreach (var variable in indexing.EnumerateObject())
                    {
                        if (variable.Value.ValueKind == JsonValueKind.Object &&
                            (isYAxis || IsCfYAxis(variable.Value)) &&
                            variable.Value.TryGetProperty("full_name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            candidate = name.GetString();
                            break;
                        }
                    }
                }

                if (candidate is null)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(candidate) || candidate.Length > 1024 || !candidate.StartsWith('/'))
                {
                    return null;
                }
                if (coordinate is not null && coordinate != candidate)
                {
                    return null; // Multiple grids need a variable-specific coordinate mapping.
                }
                coordinate = candidate;
            }

            if (coordinate is null)
            {
                return null;
            }

            var result = await runner.RunAsync("gdalmdiminfo",
                new List<string> { "-array", coordinate, "-detailed", "-limit", "2", vsiPath },
                workspace, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }
            using var details = JsonDocument.Parse(result.StandardOutput);
            if (details.RootElement.ValueKind != JsonValueKind.Object ||
                !details.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            // GDAL's bounded output is [first, "[...]", last] for longer axes.
            var endpoints = values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number)
                .Select(value => value.GetDouble()).ToArray();
            return endpoints.Length == 2 && double.IsFinite(endpoints[0]) && double.IsFinite(endpoints[1]) &&
                // codeql[cs/equality-on-floats]: equal parsed endpoints mean the axis direction is unknown; a tolerance would hide a real span
                endpoints[0] != endpoints[1] ? endpoints[1] > endpoints[0] : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsCfYAxis(JsonElement coordinate)
    {
        // The HDF5 driver used for remote NetCDF4 preserves CF coordinate
        // attributes but does not populate the dimension's HORIZONTAL_Y type.
        if (!coordinate.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return (attributes.TryGetProperty("axis", out var axis) &&
                axis.ValueKind == JsonValueKind.String && axis.GetString() == "Y") ||
            (attributes.TryGetProperty("standard_name", out var standardName) &&
                standardName.ValueKind == JsonValueKind.String &&
                standardName.GetString() is "latitude" or "projection_y_coordinate");
    }

    private static async Task<(string? InfoJson, string? Warning)> ReadCoordinatesAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        string sourcePath,
        string? zarrPath,
        string mdimJson,
        string workspace,
        CancellationToken jobCancellation,
        CancellationToken toolCancellation)
    {
        GdalCommandResult? last = null;
        if (zarrPath is not null)
        {
            last = await RunInfoAsync(
                runner, context, zarrPath, workspace, jobCancellation, toolCancellation).ConfigureAwait(false);
            if (IsCoordinateInfo(last))
            {
                return (last!.StandardOutput, null);
            }

            // A 3D+ Zarr, or a store with several arrays, opens as a subdataset list.
            // The root document has no geotransform; the data array does.
            foreach (var subdataset in SelectZarrSubdatasets(last?.StandardOutput, zarrPath, mdimJson))
            {
                last = await RunInfoAsync(
                    runner, context, subdataset, workspace, jobCancellation, toolCancellation).ConfigureAwait(false);
                if (IsCoordinateInfo(last))
                {
                    return (last!.StandardOutput, null);
                }
            }
        }

        if (IsRemoteVsi(sourcePath))
        {
            var copied = await TryCopyToScratchAsync(runner, context, sourcePath, workspace, toolCancellation).ConfigureAwait(false);
            if (copied.Path is null)
            {
                last = copied.Result ?? last;
            }
            else
            {
                last = await RunInfoAsync(
                    runner, context, copied.Path, workspace, jobCancellation, toolCancellation).ConfigureAwait(false);
                if (IsCoordinateInfo(last))
                {
                    return (last!.StandardOutput, null);
                }
            }
        }
        else
        {
            last = await RunInfoAsync(
                runner, context, sourcePath, workspace, jobCancellation, toolCancellation).ConfigureAwait(false);
            if (IsCoordinateInfo(last))
            {
                return (last!.StandardOutput, null);
            }
        }

        return (null, DescribeEnrichmentFailure(last, workspace));
    }

    private static bool IsRemoteVsi(string path)
        => path.StartsWith("/vsi", StringComparison.Ordinal);

    private static bool IsCoordinateInfo(GdalCommandResult? result)
        => result is { Succeeded: true } &&
           !string.IsNullOrWhiteSpace(result.StandardOutput) &&
           (result.StandardOutput.Contains("geoTransform", StringComparison.Ordinal) ||
            result.StandardOutput.Contains("NETCDF_DIM_", StringComparison.Ordinal) ||
            result.StandardOutput.Contains("DIM_", StringComparison.Ordinal));

    private static string DescribeEnrichmentFailure(GdalCommandResult? result, string workspace)
    {
        if (result is { Succeeded: false })
        {
            var detail = GdalErrorSanitizer.Sanitize(result.StandardError, workspace);
            return string.IsNullOrWhiteSpace(detail)
                ? $"Coordinate enrichment failed: the GDAL read exited with code {result.ExitCode}."
                : $"Coordinate enrichment failed: {detail}";
        }

        return "Coordinate enrichment failed: gdalinfo returned no geotransform or dimension coordinates.";
    }

    private static async Task<GdalCommandResult?> RunInfoAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        string path,
        string workspace,
        CancellationToken jobCancellation,
        CancellationToken toolCancellation)
    {
        GdalCommandResult coordinates;
        try
        {
            coordinates = await RunGdalInfoOnceAsync(
                runner, context, path, workspace, includeStats: false, toolCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!IsCoordinateInfo(coordinates))
        {
            return coordinates;
        }

        // -stats reads every pixel. It has its own budget so a timeout cannot
        // discard the coordinate document, and it is not tied to the shared
        // conversion timeout.
        using var statsBudget = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation);
        statsBudget.CancelAfter(StatisticsBudget);
        try
        {
            var stats = await RunGdalInfoOnceAsync(
                runner, context, path, workspace, includeStats: true, statsBudget.Token).ConfigureAwait(false);
            if (IsCoordinateInfo(stats))
            {
                return stats;
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Statistics budget elapsed. Coordinates already succeeded.
        }

        return coordinates;
    }

    private static async Task<GdalCommandResult> RunGdalInfoOnceAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        string path,
        string workspace,
        bool includeStats,
        CancellationToken cancellationToken)
    {
        var args = InfoArguments(path, includeStats);
        await GdalCommandLog.LogCommandAsync(context, "gdalinfo", args, workspace, cancellationToken).ConfigureAwait(false);
        return await runner.RunAsync("gdalinfo", args, workspace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the Zarr classic-API subdatasets that can carry a geotransform.
    /// Coordinate arrays (rank 1) are skipped when a multidimensional data array
    /// is present. At most three candidates are returned, best first.
    /// </summary>
    private static List<string> SelectZarrSubdatasets(string? infoJson, string zarrPath, string mdimJson)
    {
        var dataArrays = ReadMdimDataArrayNames(mdimJson);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = new List<(string Name, int Score)>();
        foreach (var (name, description) in ParseSubdatasets(infoJson))
        {
            if (!IsUsableSubdatasetName(name, zarrPath) || !seen.Add(name))
            {
                continue;
            }

            var rank = BracketRank(description);
            var leaf = SubdatasetLeaf(name);
            var score = 0;
            if (leaf is not null && dataArrays.Contains(leaf))
            {
                score = 100 + rank;
            }
            else if (rank >= 2)
            {
                score = rank;
            }

            usable.Add((name, score));
        }

        if (usable.Count == 0)
        {
            return [];
        }

        usable.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.Name, right.Name);
        });

        var selected = new List<string>();
        foreach (var candidate in usable)
        {
            if (candidate.Score <= 0)
            {
                continue;
            }

            selected.Add(candidate.Name);
            if (selected.Count == 3)
            {
                break;
            }
        }

        if (selected.Count == 0 && usable.Count == 1)
        {
            selected.Add(usable[0].Name);
        }

        return selected;
    }

    private static List<(string Name, string? Description)> ParseSubdatasets(string? infoJson)
    {
        var found = new List<(string Name, string? Description)>();
        if (string.IsNullOrWhiteSpace(infoJson))
        {
            return found;
        }

        try
        {
            using var document = JsonDocument.Parse(infoJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return found;
            }

            if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("SUBDATASETS", out var domain) &&
                domain.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in domain.EnumerateObject())
                {
                    if (!property.Name.EndsWith("_NAME", StringComparison.Ordinal) ||
                        property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var descriptionKey = property.Name[..^"_NAME".Length] + "_DESC";
                    string? description = domain.TryGetProperty(descriptionKey, out var desc) && desc.ValueKind == JsonValueKind.String
                        ? desc.GetString()
                        : null;
                    var name = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        found.Add((name, description));
                    }
                }
            }

            if (document.RootElement.TryGetProperty("subdatasets", out var subdatasets) &&
                subdatasets.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in subdatasets.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string? description = entry.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                        ? desc.GetString()
                        : null;
                    found.Add((name, description));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    private static bool IsUsableSubdatasetName(string name, string zarrPath)
        => name.Length is > 0 and <= 2048 &&
           name.StartsWith("ZARR:", StringComparison.Ordinal) &&
           name.Contains(zarrPath, StringComparison.Ordinal) &&
           name.AsSpan().IndexOfAny('\r', '\n', '\0') < 0;

    private static HashSet<string> ReadMdimDataArrayNames(string mdimJson)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(mdimJson))
        {
            return names;
        }

        try
        {
            using var document = JsonDocument.Parse(mdimJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return names;
            }

            var coordinates = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("dimensions", out var dimensions) &&
                dimensions.ValueKind == JsonValueKind.Array)
            {
                foreach (var dimension in dimensions.EnumerateArray())
                {
                    if (dimension.ValueKind != JsonValueKind.Object ||
                        !dimension.TryGetProperty("indexing_variable", out var indexing))
                    {
                        continue;
                    }

                    if (indexing.ValueKind == JsonValueKind.String)
                    {
                        var leaf = SubdatasetLeaf(indexing.GetString() ?? string.Empty);
                        if (leaf is not null)
                        {
                            coordinates.Add(leaf);
                        }
                    }
                    else if (indexing.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var variable in indexing.EnumerateObject())
                        {
                            coordinates.Add(variable.Name);
                        }
                    }
                }
            }

            if (document.RootElement.TryGetProperty("arrays", out var arrays) &&
                arrays.ValueKind == JsonValueKind.Object)
            {
                foreach (var array in arrays.EnumerateObject())
                {
                    if (coordinates.Contains(array.Name))
                    {
                        continue;
                    }

                    if (array.Value.ValueKind == JsonValueKind.Object &&
                        array.Value.TryGetProperty("full_name", out var fullName) &&
                        fullName.ValueKind == JsonValueKind.String &&
                        SubdatasetLeaf(fullName.GetString() ?? string.Empty) is { } fullLeaf &&
                        coordinates.Contains(fullLeaf))
                    {
                        continue;
                    }

                    names.Add(array.Name);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return names;
    }

    private static string? SubdatasetLeaf(string name)
    {
        var marker = name.LastIndexOf("\":", StringComparison.Ordinal);
        var path = marker >= 0 ? name[(marker + 2)..] : name;
        var slash = path.LastIndexOf('/');
        var leaf = (slash >= 0 ? path[(slash + 1)..] : path).Trim();
        return leaf.Length == 0 ? null : leaf;
    }

    private static int BracketRank(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return 0;
        }

        var open = description.IndexOf('[', StringComparison.Ordinal);
        var close = open < 0 ? -1 : description.IndexOf(']', open + 1);
        if (open < 0 || close <= open + 1)
        {
            return 0;
        }

        var rank = 1;
        for (var i = open + 1; i < close; i++)
        {
            if (description[i] is 'x' or 'X')
            {
                rank++;
            }
        }

        return rank;
    }

    private static List<string> InfoArguments(string path, bool includeStats)
    {
        var args = new List<string> { "--config", "GDAL_PAM_ENABLED", "NO", "-json" };
        if (includeStats)
        {
            args.Add("-stats");
        }

        args.Add(path);
        return args;
    }

    private static async Task<(string? Path, GdalCommandResult? Result)> TryCopyToScratchAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        string sourcePath,
        string workspace,
        CancellationToken cancellationToken)
    {
        var destination = Path.Join(workspace, "coverage-source.bin");
        var args = new List<string> { "copy", sourcePath, destination };
        try
        {
            await GdalCommandLog.LogCommandAsync(context, "gdalmanage", args, workspace, cancellationToken).ConfigureAwait(false);
            var result = await runner.RunAsync("gdalmanage", args, workspace, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? (destination, result) : (null, result);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static async Task<GdalCommandResult?> RunConvertAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        string vsiIn,
        string vsiOut,
        string workspace,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-of", "Zarr", vsiIn, vsiOut };
        try
        {
            await GdalCommandLog.LogCommandAsync(context, "gdalmdimtranslate", args, workspace, cancellationToken).ConfigureAwait(false);
            return await runner.RunAsync("gdalmdimtranslate", args, workspace, cancellationToken).ConfigureAwait(false);
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

    private static string BuildCombinedArtifact(string mdimJson, string? infoJson, string? zarrRootPath, bool? yAxisAscending)
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

            if (yAxisAscending is { } ascending)
            {
                writer.WriteBoolean("yAxisAscending", ascending);
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

        [LoggerMessage(9305, LogLevel.Warning,
            "GDAL multidim coverage executor job {OperationId} completed without coordinate enrichment: {Warning}")]
        public static partial void EnrichmentFailed(ILogger logger, string operationId, string warning);
    }
}
