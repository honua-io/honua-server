// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Attempt-scoped context that replaces raster bytes with a durable publication manifest. TIFF
/// outputs are normalized to COG locally in the heavyweight GP worker, then streamed to the
/// configured store. Redis sees only the bounded manifest marker.
/// </summary>
internal sealed partial class RasterStagingJobExecutionContext :
    IJobExecutionContext,
    IGdalFileArtifactPublishingContext,
    IAsyncDisposable
{
    private const string TiffContentType = "image/tiff";
    private const string DataUriPrefix = "data:image/tiff;base64,";
    private static readonly TimeSpan StagingTimeToLive = TimeSpan.FromHours(24);
    private readonly ExecutionJobRecord _job;
    private readonly IJobExecutionContext _inner;
    private readonly IRasterOutputObjectStore _objectStore;
    private readonly IRasterOutputManifestStore _manifestStore;
    private readonly IGdalCommandRunner _runner;
    private readonly ILogger _logger;
    private readonly string _storeReference;
    private readonly string _manifestObjectKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<StagedRasterOutputDescriptor> _outputs = [];
    private bool _manifestMarkerPublished;
    private string? _engineVersion;

    public RasterStagingJobExecutionContext(
        ExecutionJobRecord job,
        IJobExecutionContext inner,
        IRasterOutputObjectStore objectStore,
        IRasterOutputManifestStore manifestStore,
        IGdalCommandRunner runner,
        ILogger logger,
        string storeReference)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storeReference = RasterOutputWorkerContract.IsLogicalStoreReference(storeReference)
            ? storeReference
            : throw new ArgumentException("Raster output store reference is invalid.", nameof(storeReference));
        _manifestObjectKey = RasterOutputWorkerContract.BuildManifestObjectKey(job.OperationId, job.AttemptCount);
    }

    public string OperationId => _inner.OperationId;

    public Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default) =>
        _inner.ReportProgressAsync(percentComplete, phase, cancellationToken);

    public Task AppendLogAsync(
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default) =>
        _inner.AppendLogAsync(entry, cancellationToken);

    /// <summary>
    /// Compatibility interception for raster executors not yet using direct file publication. The
    /// data URI is decoded only in the GDAL worker and immediately routed through the same staged
    /// COG path; it is never forwarded to the durable job store.
    /// </summary>
    public async Task PublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactReference);
        if (!artifactReference.StartsWith(DataUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _inner.PublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporary = Path.Join(
            Path.GetTempPath(),
            "honua-gdal-raster-publication",
            _job.OperationId,
            $"attempt-{_job.AttemptCount}",
            $"legacy-{Guid.NewGuid():N}.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            var payload = Convert.FromBase64String(artifactReference[DataUriPrefix.Length..]);
            await File.WriteAllBytesAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
            var result = await PublishFileArtifactAsync(
                temporary,
                TiffContentType,
                long.MaxValue,
                "Raster output",
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidDataException(result.ErrorMessage);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<GdalArtifactPublicationResult> PublishFileArtifactAsync(
        string path,
        string contentType,
        long maximumInlineBytes,
        string artifactLabel,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return new GdalArtifactPublicationResult(0, $"{artifactLabel} is empty.");
        }

        // Only TIFF has a canonical durable encoding in output contract v1. Other formats remain
        // deliberately bounded inline artifacts until the contract grows another durable encoding.
        if (!string.Equals(contentType, TiffContentType, StringComparison.OrdinalIgnoreCase))
        {
            if (info.Length > maximumInlineBytes)
            {
                return new GdalArtifactPublicationResult(
                    info.Length,
                    $"{artifactLabel} size {info.Length} bytes exceeds configured MaxArtifactBytes={maximumInlineBytes}.");
            }

            var inlineBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            await _inner.PublishArtifactAsync(
                GdalDataUri.Build(contentType, inlineBytes),
                cancellationToken).ConfigureAwait(false);
            return new GdalArtifactPublicationResult(inlineBytes.LongLength);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StageTiffAsync(path, artifactLabel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not OperationCanceledException)
        {
            Log.StageFailed(_logger, _job.OperationId, exception);
            return new GdalArtifactPublicationResult(
                info.Length,
                "Raster output could not be staged for durable publication.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<GdalArtifactPublicationResult> StageTiffAsync(
        string sourcePath,
        string artifactLabel,
        CancellationToken cancellationToken)
    {
        var cogPath = sourcePath + $".honua-{Guid.NewGuid():N}.tif";
        try
        {
            var workspace = Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath();
            var conversion = await _runner.RunAsync(
                "gdal_translate",
                ["-of", "COG", sourcePath, cogPath],
                workspace,
                cancellationToken).ConfigureAwait(false);
            if (!conversion.Succeeded || !File.Exists(cogPath))
            {
                return new GdalArtifactPublicationResult(
                    new FileInfo(sourcePath).Length,
                    "Raster output could not be normalized to Cloud Optimized GeoTIFF.");
            }

            var grid = await ReadGridAsync(cogPath, workspace, cancellationToken).ConfigureAwait(false);
            var fileInfo = new FileInfo(cogPath);
            if (fileInfo.Length <= 0)
            {
                return new GdalArtifactPublicationResult(0, "Normalized raster output is empty.");
            }

            var checksum = await ComputeSha256Async(cogPath, cancellationToken).ConfigureAwait(false);
            var outputName = UniqueOutputName(_outputs);
            var now = DateTimeOffset.UtcNow;
            var descriptor = new StagedRasterOutputDescriptor
            {
                JobId = _job.OperationId,
                Attempt = _job.AttemptCount,
                OutputName = outputName,
                StoreReference = _storeReference,
                ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey(
                    _job.OperationId,
                    _job.AttemptCount,
                    outputName),
                Content = new RasterContentIdentity
                {
                    SizeBytes = fileInfo.Length,
                    MediaType = TiffContentType,
                    Checksum = new RasterChecksum("sha256", checksum)
                },
                Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
                Grid = grid,
                Engine = new RasterProducingEngine("gdal", await GetEngineVersionAsync(
                    workspace,
                    cancellationToken).ConfigureAwait(false)),
                Lineage = new RasterOutputLineage
                {
                    JobId = _job.OperationId,
                    Attempt = _job.AttemptCount,
                    ProcessId = GdalJobInputReader.ResolveProcessId(_job.Spec.Parameters) ?? "unknown"
                },
                CreatedAt = now,
                ExpiresAt = now + StagingTimeToLive
            };

            await using (var content = new FileStream(
                cogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _objectStore.StageAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
            }

            _outputs.Add(descriptor);
            var manifest = new RasterOutputPublicationManifest
            {
                JobId = _job.OperationId,
                Attempt = _job.AttemptCount,
                CreatedAt = DateTimeOffset.UtcNow,
                Outputs = _outputs.ToArray()
            };
            await _manifestStore.WriteManifestAsync(
                _storeReference,
                _manifestObjectKey,
                manifest,
                cancellationToken).ConfigureAwait(false);

            if (!_manifestMarkerPublished)
            {
                await _inner.PublishArtifactAsync(
                    RasterOutputArtifactReference.CreateManifest(_storeReference, _manifestObjectKey),
                    cancellationToken).ConfigureAwait(false);
                _manifestMarkerPublished = true;
            }

            Log.Staged(_logger, _job.OperationId, outputName, fileInfo.Length);
            return new GdalArtifactPublicationResult(fileInfo.Length);
        }
        finally
        {
            if (File.Exists(cogPath))
            {
                File.Delete(cogPath);
            }
        }
    }

    private async Task<RasterGridMetadata> ReadGridAsync(
        string path,
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "gdalinfo",
            ["-json", path],
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidDataException("gdalinfo could not inspect the staged raster output.");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        if (!root.TryGetProperty("size", out var size)
            || size.ValueKind != JsonValueKind.Array
            || size.GetArrayLength() != 2
            || !root.TryGetProperty("bands", out var bands)
            || bands.ValueKind != JsonValueKind.Array
            || bands.GetArrayLength() == 0
            || !root.TryGetProperty("geoTransform", out var transform)
            || transform.ValueKind != JsonValueKind.Array
            || transform.GetArrayLength() != 6)
        {
            throw new InvalidDataException("gdalinfo output is missing required grid metadata.");
        }

        var coefficients = transform.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var crs = ResolveCrs(root);
        return new RasterGridMetadata
        {
            Width = size[0].GetInt64(),
            Height = size[1].GetInt64(),
            BandCount = bands.GetArrayLength(),
            GeoTransform = coefficients,
            Crs = crs
        };
    }

    private async Task<string> GetEngineVersionAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        if (_engineVersion is not null)
        {
            return _engineVersion;
        }

        var result = await _runner.RunAsync(
            "gdalinfo",
            ["--version"],
            workspace,
            cancellationToken).ConfigureAwait(false);
        var value = result.Succeeded
            ? string.Join(' ', result.StandardOutput.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : "unknown";
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "unknown";
        }

        _engineVersion = value.Length is > 0 and <= 128 ? value : value[..Math.Min(value.Length, 128)];
        return _engineVersion;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var content = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static string ResolveCrs(JsonElement root)
    {
        if (!root.TryGetProperty("coordinateSystem", out var coordinateSystem)
            || !coordinateSystem.TryGetProperty("wkt", out var wktElement)
            || string.IsNullOrWhiteSpace(wktElement.GetString()))
        {
            return "unknown";
        }

        var wkt = wktElement.GetString()!;
        var matches = EpsgAuthorityRegex().Matches(wkt);
        if (matches.Count > 0)
        {
            return "EPSG:" + matches[^1].Groups[1].Value;
        }

        var normalized = string.Join(' ', wkt.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(normalized.Length, 4096)];
    }

    private static string UniqueOutputName(
        IReadOnlyCollection<StagedRasterOutputDescriptor> existing)
    {
        const string safeStem = "result";
        var candidate = safeStem + ".tif";
        var suffix = 2;
        while (existing.Any(output => string.Equals(output.OutputName, candidate, StringComparison.Ordinal)))
        {
            candidate = $"{safeStem}-{suffix++}.tif";
        }

        return candidate;
    }

    [GeneratedRegex("(?:AUTHORITY|ID)\\s*\\[\\s*\"EPSG\"\\s*,\\s*\"?(\\d+)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex EpsgAuthorityRegex();

    private static partial class Log
    {
        [LoggerMessage(9294, LogLevel.Information,
            "Staged raster output for job {OperationId}: output={OutputName}, bytes={Bytes}")]
        public static partial void Staged(ILogger logger, string operationId, string outputName, long bytes);

        [LoggerMessage(9295, LogLevel.Error,
            "Failed to stage raster output for job {OperationId}")]
        public static partial void StageFailed(ILogger logger, string operationId, Exception exception);
    }
}
