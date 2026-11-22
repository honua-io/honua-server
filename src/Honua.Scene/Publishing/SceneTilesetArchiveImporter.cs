// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

internal sealed partial class SceneTilesetArchiveImporter
{
    internal const int DefaultMaxEntryCount = 10_000;
    internal const long DefaultMaxUncompressedBytes = 512L * 1024 * 1024;
    internal const long DefaultMaxSingleEntryBytes = 128L * 1024 * 1024;
    internal const int DefaultMaxPathDepth = 32;
    internal const int DefaultMaxRelativePathLength = 512;

    private const string ThreeTzIndexEntryName = "@3dtilesIndex1@";
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixSymlinkType = 0xA000;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".glb",
        ".gltf",
        ".b3dm",
        ".i3dm",
        ".pnts",
        ".cmpt",
        ".bin",
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".ktx",
        ".ktx2",
        ".basis",
        ".subtree",
        ".geom",
        ".vctr",
    };

    private static readonly SearchValues<char> ForbiddenPathChars =
        SearchValues.Create(['\0', ':', ';', '|', '&', '$', '<', '>', '`', '\n', '\r', '*', '?', '"', '\'']);

    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<SceneTilesetArchiveImporter> _logger;
    private readonly ISceneRegistrationService? _registration;
    private readonly ICloudFileStorage? _storage;

    public SceneTilesetArchiveImporter(
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<SceneTilesetArchiveImporter> logger,
        ISceneRegistrationService? registration = null,
        ICloudFileStorage? storage = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
        _storage = storage;
    }

    public async Task<SceneTilesetArchiveImportOutcome> ImportAsync(
        SceneTilesetArchiveImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ArchiveBytes);

        if (request.ArchiveBytes.Length == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive payload must not be empty.");
        }

        var serverOptions = _options.Value;
        var limits = request.Limits ?? SceneTilesetArchiveImportOptions.Default;
        var intentId = Guid.NewGuid().ToString("N");
        var tilesetFileName = string.IsNullOrWhiteSpace(request.TilesetFileName)
            ? "tileset.json"
            : request.TilesetFileName.Trim();

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration,
            ActivityKind.Internal);
        activity?.SetTag("honua.scene.intent_id", intentId);
        activity?.SetTag("honua.scene.source_kind", "archive");

        ArchiveImportLog.Started(_logger, intentId, request.ArchiveBytes.Length);
        var stopwatch = Stopwatch.StartNew();

        var sceneId = ScenePublishHelper.ResolveSceneId(request.SceneId, "archive", intentId);
        var displayName = ScenePublishHelper.ResolveDisplayName(request.DisplayName, "Imported 3D Tiles", sceneId);
        ScenePublishHelper.ValidateCommonOptions(
            displayName,
            tilesetFileName,
            request.EditionGate,
            request.CacheMaxAgeSeconds,
            extent: null,
            request.RequiresAuth);

        var cacheMaxAge = request.CacheMaxAgeSeconds ?? ScenePublishHelper.DefaultCacheMaxAgeSeconds;
        await ScenePublishHelper.PreflightConflictAsync(_registration, sceneId, cancellationToken).ConfigureAwait(false);

        var outputDirectory = SceneTilesetStaging.ResolveOutputDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, sceneId);
        var stagingDirectory = SceneTilesetStaging.ResolveStagingDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, intentId);
        string? stagingToCleanup = stagingDirectory;

        try
        {
            SceneOutputGuard.Run(() => Directory.CreateDirectory(stagingDirectory));
            var extracted = await ExtractArchiveSafelyAsync(
                request.ArchiveBytes,
                stagingDirectory,
                tilesetFileName,
                limits,
                cancellationToken).ConfigureAwait(false);

            var inspection = ValidateExtractedTileset(
                stagingDirectory,
                tilesetFileName,
                extracted.ExtractedFiles);

            ScenePublishHelper.ValidateCommonOptions(
                displayName,
                tilesetFileName,
                request.EditionGate,
                request.CacheMaxAgeSeconds,
                inspection.Extent,
                request.RequiresAuth);

            var commit = new ScenePublishCommitContext(
                Registration: _registration,
                Storage: _storage,
                SceneId: sceneId,
                DisplayName: displayName,
                Description: request.Description,
                StagingDirectory: stagingDirectory,
                OutputDirectory: outputDirectory,
                TilesetFileName: tilesetFileName,
                DatasetType: SceneDatasetType.HostedTiles,
                Extent: inspection.Extent,
                Crs: "EPSG:4979",
                CacheMaxAgeSeconds: cacheMaxAge,
                EditionGate: request.EditionGate,
                RequiresAuth: request.RequiresAuth,
                CreatedBy: request.CreatedBy ?? "publisher",
                OnOverwroteStaleFinalDir: stale => ArchiveImportLog.PromotionOverwroteStaleFinalDir(_logger, stale),
                OnRegistrationCompensated: (id, deactivated) => ArchiveImportLog.RegistrationCompensated(_logger, sceneId, id, deactivated),
                OnRegistrationCompensationFailed: (id, ex) => ArchiveImportLog.RegistrationCompensationFailed(_logger, sceneId, id, ex));

            try
            {
                await ScenePublishHelper.UploadRegisterAndPromoteAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            stagingToCleanup = null;
            stopwatch.Stop();

            activity?.SetTag("honua.scene.id", sceneId);
            activity?.SetTag("honua.scene.file_count", extracted.FileCount);
            ArchiveImportLog.Completed(
                _logger, intentId, sceneId, extracted.FileCount, extracted.TotalBytes, stopwatch.ElapsedMilliseconds);

            return new SceneTilesetArchiveImportOutcome(
                sceneId,
                outputDirectory,
                tilesetFileName,
                inspection.Extent,
                extracted.FileCount,
                extracted.TotalBytes,
                inspection.TilesetVersion,
                inspection.Warnings);
        }
        finally
        {
            if (stagingToCleanup is not null)
            {
                SceneTilesetStaging.TryDeleteStaging(
                    stagingToCleanup,
                    ex => ArchiveImportLog.StagingCleanupFailed(_logger, stagingToCleanup, ex));
            }
        }
    }

    private static async Task<ExtractedArchiveResult> ExtractArchiveSafelyAsync(
        byte[] archiveBytes,
        string stagingDirectory,
        string tilesetFileName,
        SceneTilesetArchiveImportOptions limits,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(archiveBytes, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive is not a valid ZIP/3TZ container.",
                ex);
        }

        using (archive)
        {
            var candidates = CollectAndValidateEntries(archive, limits);
            var wrapperPrefix = ResolveCommonRootWrapper(candidates, tilesetFileName);

            var canonicalStagingRoot = Path.GetFullPath(stagingDirectory);
            var rootPrefixWithSeparator = canonicalStagingRoot.EndsWith(Path.DirectorySeparatorChar)
                ? canonicalStagingRoot
                : canonicalStagingRoot + Path.DirectorySeparatorChar;

            long totalBytes = 0;
            var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[81920];

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = StripWrapperPrefix(candidate.NormalizedPath, wrapperPrefix);
                if (relativePath.Length == 0)
                {
                    continue;
                }

                ValidateRelativeEntryPath(relativePath, limits);
                ValidateAllowedExtension(relativePath);

                if (!extractedPaths.Add(relativePath))
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive contains duplicate entry '{relativePath}'.");
                }

                var destinationPath = Path.GetFullPath(
                    Path.Join(canonicalStagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destinationPath.StartsWith(rootPrefixWithSeparator, StringComparison.Ordinal))
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{candidate.Entry.FullName}' escapes the staging root.");
                }

                if (candidate.Entry.Length > limits.MaxSingleEntryBytes)
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' exceeds the {limits.MaxSingleEntryBytes}-byte single-entry limit.");
                }

                if (candidate.Entry.Length > limits.MaxUncompressedBytes - totalBytes)
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive exceeds the {limits.MaxUncompressedBytes}-byte uncompressed budget.");
                }

                var parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    SceneOutputGuard.Run(() => Directory.CreateDirectory(parentDir));
                }

                long entryBytes = 0;
                await using var entryStream = candidate.Entry.Open();
                await using var fileStream = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                while (true)
                {
                    var read = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    entryBytes += read;
                    totalBytes += read;
                    if (entryBytes > limits.MaxSingleEntryBytes)
                    {
                        throw new ValidationException(
                            $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' exceeds the {limits.MaxSingleEntryBytes}-byte uncompressed entry cap.");
                    }

                    if (totalBytes > limits.MaxUncompressedBytes)
                    {
                        throw new ValidationException(
                            $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive exceeds the {limits.MaxUncompressedBytes}-byte uncompressed byte budget.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (extractedPaths.Count == 0)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive contains no extractable 3D Tiles files.");
            }

            return new ExtractedArchiveResult(extractedPaths, extractedPaths.Count, totalBytes);
        }
    }

    private static List<ValidatedZipEntry> CollectAndValidateEntries(
        ZipArchive archive,
        SceneTilesetArchiveImportOptions limits)
    {
        var entries = new List<ValidatedZipEntry>();
        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            if (IsSymlinkEntry(entry))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive contains a symbolic link entry '{entry.FullName}', which is not permitted.");
            }

            var normalized = NormalizeRawArchivePath(entry.FullName, limits);
            if (string.Equals(normalized, ThreeTzIndexEntryName, StringComparison.Ordinal))
            {
                // OGC 3TZ archives include a binary root index entry for random-access lookup.
                continue;
            }

            entries.Add(new ValidatedZipEntry(entry, normalized));
            if (entries.Count > limits.MaxEntryCount)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive exceeds the {limits.MaxEntryCount}-entry cap.");
            }
        }

        return entries;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => string.IsNullOrEmpty(entry.Name)
            && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'));

    private static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        var unixMode = (uint)(entry.ExternalAttributes >> 16);
        return (unixMode & UnixFileTypeMask) == UnixSymlinkType;
    }

    private static string NormalizeRawArchivePath(string rawPath, SceneTilesetArchiveImportOptions limits)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive contains an empty entry path.");
        }

        if (rawPath.StartsWith('/') || rawPath.StartsWith('\\') || Path.IsPathRooted(rawPath))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{rawPath}' must be a relative path.");
        }

        var unified = rawPath.Replace('\\', '/');
        ValidateRelativeEntryPath(unified, limits);
        return unified;
    }

    private static void ValidateRelativeEntryPath(string relativePath, SceneTilesetArchiveImportOptions limits)
    {
        if (relativePath.Length > limits.MaxRelativePathLength)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry path exceeds the {limits.MaxRelativePathLength}-character limit.");
        }

        if (relativePath.AsSpan().IndexOfAny(ForbiddenPathChars) >= 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' contains forbidden characters.");
        }

        var segments = relativePath.Split('/');
        if (segments.Length > limits.MaxPathDepth)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' exceeds the maximum directory depth of {limits.MaxPathDepth}.");
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' contains unsafe path traversal segments.");
            }
        }
    }

    private static void ValidateAllowedExtension(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' uses disallowed extension '{extension}'.");
        }
    }

    private static string? ResolveCommonRootWrapper(
        List<ValidatedZipEntry> entries,
        string tilesetFileName)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Any(e => string.Equals(e.NormalizedPath, tilesetFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string? commonTop = null;
        foreach (var entry in entries)
        {
            var slash = entry.NormalizedPath.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                return null;
            }

            var top = entry.NormalizedPath[..slash];
            if (commonTop is null)
            {
                commonTop = top;
            }
            else if (!string.Equals(commonTop, top, StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (commonTop is null)
        {
            return null;
        }

        var expectedNested = commonTop + "/" + tilesetFileName;
        return entries.Any(e => string.Equals(e.NormalizedPath, expectedNested, StringComparison.OrdinalIgnoreCase))
            ? commonTop + "/"
            : null;
    }

    private static string StripWrapperPrefix(string normalizedPath, string? wrapperPrefix)
        => wrapperPrefix is not null && normalizedPath.StartsWith(wrapperPrefix, StringComparison.Ordinal)
            ? normalizedPath[wrapperPrefix.Length..]
            : normalizedPath;

    private static TilesetInspectionResult ValidateExtractedTileset(
        string stagingDirectory,
        string tilesetFileName,
        HashSet<string> extractedFiles)
    {
        var exactPaths = extractedFiles.ToHashSet(StringComparer.Ordinal);
        if (!exactPaths.Contains(tilesetFileName))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive does not contain a root '{tilesetFileName}'.");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        return Visit(tilesetFileName, 0);

        TilesetInspectionResult Visit(string path, int depth)
        {
            if (depth > 16 || !active.Add(path))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: External tileset references contain a cycle or exceed the supported depth.");
            }

            var inspection = InspectTileset(stagingDirectory, path, uri =>
            {
                var target = ResolveContentPath(path, uri);
                if (!exactPaths.Contains(target))
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.OptionsInvalid}: Tileset '{path}' references missing content file '{uri}'.");
                }

                if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !visited.Contains(target))
                {
                    Visit(target, depth + 1);
                }
            });

            active.Remove(path);
            visited.Add(path);
            return inspection;
        }
    }

    private static TilesetInspectionResult InspectTileset(
        string stagingDirectory,
        string tilesetFileName,
        Action<string> validateContentUri)
    {
        var tilesetPath = Path.Join(stagingDirectory, tilesetFileName);
        if (!File.Exists(tilesetPath))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive does not contain a root '{tilesetFileName}'.");
        }

        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(tilesetPath);
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Root '{tilesetFileName}' is not valid JSON.",
                ex);
        }

        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Root '{tilesetFileName}' must be a JSON object.");
            }

            if (!rootElement.TryGetProperty("asset", out var asset)
                || asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("version", out var versionProp)
                || versionProp.ValueKind != JsonValueKind.String
                || versionProp.GetString() is not { Length: > 0 } version)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Tileset must declare a non-empty asset.version.");
            }

            if (version is not ("1.0" or "1.1"))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Unsupported 3D Tiles version '{version}'. Expected '1.0' or '1.1'.");
            }

            if (!rootElement.TryGetProperty("root", out var tileRoot) || tileRoot.ValueKind != JsonValueKind.Object)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Tileset must declare a root tile object.");
            }

            if (!tileRoot.TryGetProperty("boundingVolume", out var boundingVolume)
                || boundingVolume.ValueKind != JsonValueKind.Object)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: Root tile must declare a boundingVolume.");
            }

            var warnings = new List<string>();
            var extent = TryExtractExtent(tileRoot, boundingVolume, warnings);
            ValidateContentReferences(tileRoot, validateContentUri, depth: 0);

            return new TilesetInspectionResult(version, extent, warnings.AsReadOnly());
        }
    }

    private static void ValidateContentReferences(
        JsonElement tile,
        Action<string> validateContentUri,
        int depth)
    {
        if (depth > 128)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Tileset node hierarchy exceeds maximum supported depth.");
        }

        if (tile.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            ValidateSingleContentUri(content, validateContentUri);
        }

        if (tile.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in contents.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    ValidateSingleContentUri(entry, validateContentUri);
                }
            }
        }

        if (tile.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    ValidateContentReferences(child, validateContentUri, depth + 1);
                }
            }
        }
    }

    private static void ValidateSingleContentUri(JsonElement content, Action<string> validateContentUri)
    {
        string? uri = null;
        if (content.TryGetProperty("uri", out var uriProp) && uriProp.ValueKind == JsonValueKind.String)
        {
            uri = uriProp.GetString();
        }
        else if (content.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
        {
            uri = urlProp.GetString();
        }

        if (!string.IsNullOrWhiteSpace(uri))
        {
            validateContentUri(uri);
        }
    }

    private static string ResolveContentPath(string documentPath, string uri)
    {
        ValidationException InvalidPath() => new(
            $"{SceneGenerationErrorCodes.OptionsInvalid}: Tileset content URI '{uri}' must be a safe archive-relative path.");

        var decoded = Uri.UnescapeDataString(uri.Split('?', '#')[0]);
        if (decoded.Length == 0 || decoded.StartsWith('/') || decoded.Contains('\\') || decoded.Contains(':'))
        {
            throw InvalidPath();
        }

        var segments = documentPath.Split('/').SkipLast(1).ToList();
        foreach (var segment in decoded.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw InvalidPath();
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static SceneExtent? TryExtractExtent(
        JsonElement tileRoot,
        JsonElement boundingVolume,
        List<string> warnings)
    {
        if (boundingVolume.TryGetProperty("region", out var region)
            && region.ValueKind == JsonValueKind.Array
            && region.GetArrayLength() >= 6)
        {
            var west = region[0].GetDouble() * 180.0 / Math.PI;
            var south = region[1].GetDouble() * 180.0 / Math.PI;
            var east = region[2].GetDouble() * 180.0 / Math.PI;
            var north = region[3].GetDouble() * 180.0 / Math.PI;
            var minH = region[4].GetDouble();
            var maxH = region[5].GetDouble();
            return new SceneExtent(west, south, east, north, minH, maxH);
        }

        var transform = tileRoot.TryGetProperty("transform", out _)
            ? SceneAffineTransform.Read(tileRoot, "transform")
            : SceneAffineTransform.Identity;

        if (boundingVolume.TryGetProperty("box", out var box)
            && box.ValueKind == JsonValueKind.Array
            && box.GetArrayLength() == 12)
        {
            var values = box.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var center = new SceneCartesian(values[0], values[1], values[2]);
            var xAxis = new SceneCartesian(values[3], values[4], values[5]);
            var yAxis = new SceneCartesian(values[6], values[7], values[8]);
            var zAxis = new SceneCartesian(values[9], values[10], values[11]);
            return TryCornersToExtent(transform, center, xAxis, yAxis, zAxis);
        }

        if (boundingVolume.TryGetProperty("sphere", out var sphere)
            && sphere.ValueKind == JsonValueKind.Array
            && sphere.GetArrayLength() == 4)
        {
            var values = sphere.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var center = new SceneCartesian(values[0], values[1], values[2]);
            var radius = Math.Abs(values[3]);
            return TryCornersToExtent(
                transform,
                center,
                new SceneCartesian(radius, 0, 0),
                new SceneCartesian(0, radius, 0),
                new SceneCartesian(0, 0, radius));
        }

        warnings.Add("Tileset root boundingVolume did not yield a geographic extent.");
        return null;
    }

    private static SceneExtent? TryCornersToExtent(
        double[] transform,
        SceneCartesian center,
        SceneCartesian xAxis,
        SceneCartesian yAxis,
        SceneCartesian zAxis)
    {
        var minLon = double.PositiveInfinity;
        var minLat = double.PositiveInfinity;
        var maxLon = double.NegativeInfinity;
        var maxLat = double.NegativeInfinity;
        var minH = double.PositiveInfinity;
        var maxH = double.NegativeInfinity;

        for (var sx = -1; sx <= 1; sx += 2)
        {
            for (var sy = -1; sy <= 1; sy += 2)
            {
                for (var sz = -1; sz <= 1; sz += 2)
                {
                    var local = new SceneCartesian(
                        center.X + sx * xAxis.X + sy * yAxis.X + sz * zAxis.X,
                        center.Y + sx * xAxis.Y + sy * yAxis.Y + sz * zAxis.Y,
                        center.Z + sx * xAxis.Z + sy * yAxis.Z + sz * zAxis.Z);
                    var world = SceneAffineTransform.Apply(transform, local);
                    if (!world.IsFinite || world.Length < 1000.0)
                    {
                        return null;
                    }

                    var (lon, lat, height) = EcefCoordinateTransform.FromEcef(world.X, world.Y, world.Z);
                    if (lon < minLon) minLon = lon;
                    if (lat < minLat) minLat = lat;
                    if (lon > maxLon) maxLon = lon;
                    if (lat > maxLat) maxLat = lat;
                    if (height < minH) minH = height;
                    if (height > maxH) maxH = height;
                }
            }
        }

        return double.IsFinite(minLon) && double.IsFinite(maxLon)
            ? new SceneExtent(minLon, minLat, maxLon, maxLat, minH, maxH)
            : null;
    }

    private readonly record struct ValidatedZipEntry(ZipArchiveEntry Entry, string NormalizedPath);

    private readonly record struct ExtractedArchiveResult(
        HashSet<string> ExtractedFiles,
        int FileCount,
        long TotalBytes);

    private readonly record struct TilesetInspectionResult(
        string TilesetVersion,
        SceneExtent? Extent,
        IReadOnlyList<string> Warnings);

    internal static partial class ArchiveImportLog
    {
        [LoggerMessage(EventId = 8490, Level = LogLevel.Information,
            Message = "3D Tiles archive import started: intent {IntentId}, archive {ByteCount} bytes")]
        public static partial void Started(ILogger logger, string intentId, int byteCount);

        [LoggerMessage(EventId = 8491, Level = LogLevel.Information,
            Message = "3D Tiles archive import completed: intent {IntentId}, scene {SceneId}, files {FileCount}, bytes {TotalBytes}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, int fileCount, long totalBytes, long elapsedMs);

        [LoggerMessage(EventId = 8492, Level = LogLevel.Warning,
            Message = "3D Tiles archive import overwrote stale final directory {FinalDirectory} during staging promotion.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8493, Level = LogLevel.Warning,
            Message = "3D Tiles archive import could not delete staging directory {StagingDirectory}.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8494, Level = LogLevel.Warning,
            Message = "3D Tiles archive import deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8495, Level = LogLevel.Error,
            Message = "3D Tiles archive import could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);
    }
}

internal sealed record SceneTilesetArchiveImportOptions(
    int MaxEntryCount = SceneTilesetArchiveImporter.DefaultMaxEntryCount,
    long MaxUncompressedBytes = SceneTilesetArchiveImporter.DefaultMaxUncompressedBytes,
    long MaxSingleEntryBytes = SceneTilesetArchiveImporter.DefaultMaxSingleEntryBytes,
    int MaxPathDepth = SceneTilesetArchiveImporter.DefaultMaxPathDepth,
    int MaxRelativePathLength = SceneTilesetArchiveImporter.DefaultMaxRelativePathLength)
{
    public static SceneTilesetArchiveImportOptions Default { get; } = new();
}

internal sealed record SceneTilesetArchiveImportRequest(
    byte[] ArchiveBytes,
    string? SceneId = null,
    string? DisplayName = null,
    string? Description = null,
    string? TilesetFileName = null,
    string? EditionGate = null,
    int? CacheMaxAgeSeconds = null,
    bool RequiresAuth = false,
    string? CreatedBy = null,
    SceneTilesetArchiveImportOptions? Limits = null);

internal sealed record SceneTilesetArchiveImportOutcome(
    string SceneId,
    string AssetRoot,
    string TilesetFileName,
    SceneExtent? Extent,
    int FileCount,
    long TotalBytes,
    string TilesetVersion,
    IReadOnlyList<string> Warnings);
