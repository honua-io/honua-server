// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static bool IsZipFileName(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsKmzFileName(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".kmz", StringComparison.OrdinalIgnoreCase);

    private async Task<ShapefileScratch> PrepareShapefileScratchAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!IsZipFileName(fileName))
        {
            throw new NotSupportedException("Shapefile imports require a .zip containing .shp and .dbf files.");
        }

        var scratchDir = Path.Combine(_shapefileScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.zip");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var entries = SelectShapefileEntries(archive)
                ?? throw new InvalidDataException("Zip does not contain required .shp and .dbf files.");
            var extractionBudget = CreateArchiveExtractionBudget();

            var shpPath = Path.Combine(scratchDir, entries.BaseName + ".shp");
            var dbfPath = Path.Combine(scratchDir, entries.BaseName + ".dbf");

            await ExtractEntryAsync(entries.ShpEntry, shpPath, extractionBudget, cancellationToken);
            await ExtractEntryAsync(entries.DbfEntry, dbfPath, extractionBudget, cancellationToken);

            if (entries.ShxEntry != null)
            {
                await ExtractEntryAsync(entries.ShxEntry, Path.Combine(scratchDir, entries.BaseName + ".shx"), extractionBudget, cancellationToken);
            }

            if (entries.PrjEntry != null)
            {
                await ExtractEntryAsync(entries.PrjEntry, Path.Combine(scratchDir, entries.BaseName + ".prj"), extractionBudget, cancellationToken);
            }

            if (entries.CpgEntry != null)
            {
                await ExtractEntryAsync(entries.CpgEntry, Path.Combine(scratchDir, entries.BaseName + ".cpg"), extractionBudget, cancellationToken);
            }

            return new ShapefileScratch(scratchDir, shpPath);
        }
        catch
        {
            CleanupShapefileScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    ShapefileLog.DeleteZipFailed(_logger, ex, zipPath);
                }
            }
        }
    }

    private async Task<GeoPackageScratch> PrepareGeoPackageScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_geoPackageScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        var filePath = Path.Combine(scratchDir, "upload.gpkg");

        try
        {
            if (stream.CanSeek && stream.Position != 0)
            {
                stream.Position = 0;
            }

            await using var outputStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await stream.CopyToAsync(outputStream, cancellationToken);

            return new GeoPackageScratch(scratchDir, filePath);
        }
        catch
        {
            CleanupGeoPackageScratchDirectory(scratchDir);
            throw;
        }
    }

    private async Task<KmzScratch> PrepareKmzScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_kmzScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.kmz");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var kmlEntry = SelectKmzKmlEntry(archive)
                ?? throw new InvalidDataException("KMZ does not contain a .kml file.");
            var extractionBudget = CreateArchiveExtractionBudget();

            var kmlPath = Path.Combine(scratchDir, "doc.kml");
            await ExtractEntryAsync(kmlEntry, kmlPath, extractionBudget, cancellationToken);

            return new KmzScratch(scratchDir, kmlPath);
        }
        catch
        {
            CleanupKmzScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    KmzLog.DeleteZipFailed(_logger, ex, zipPath);
                }
            }
        }
    }

    /// <summary>
    /// Buffer a GeoParquet stream to a seekable stream if needed.
    /// </summary>
    private async Task<GeoParquetScratch> PrepareGeoParquetScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            if (stream.Position != 0)
            {
                stream.Position = 0;
            }

            return new GeoParquetScratch(stream, null);
        }

        var scratchDir = Path.Combine(_geoParquetScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        var filePath = Path.Combine(scratchDir, "upload.parquet");

        try
        {
            await using var outputStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await stream.CopyToAsync(outputStream, cancellationToken);

            var readStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess
            });

            return new GeoParquetScratch(readStream, scratchDir);
        }
        catch
        {
            CleanupGeoParquetScratchDirectory(scratchDir);
            throw;
        }
    }

    /// <summary>
    /// Extract a FileGDB .gdb.zip archive to a scratch directory.
    /// </summary>
    private async Task<FileGdbScratch> PrepareFileGdbScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_fileGdbScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.gdb.zip");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var extractionBudget = CreateArchiveExtractionBudget();

            // Extract all entries, maintaining directory structure
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                // Security: prevent path traversal with multiple layers of protection
                if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.Contains("..") ||
                    entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\'))
                {
                    throw new InvalidDataException("Archive contains invalid entry name.");
                }

                // The trailing separator ensures a sibling directory that shares a prefix cannot pass the check.
                var normalizedRoot = scratchDir.EndsWith(Path.DirectorySeparatorChar)
                    ? scratchDir
                    : scratchDir + Path.DirectorySeparatorChar;
                var entryPath = Path.GetFullPath(Path.Combine(scratchDir, entry.FullName));
                if (!entryPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Archive contains path traversal.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                await ExtractEntryAsync(entry, entryPath, extractionBudget, cancellationToken);
            }

            // Find the .gdb directory
            var gdbDir = Directory.GetDirectories(scratchDir, "*.gdb", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (gdbDir == null)
            {
                // Maybe the files are at the root level (no subfolder)
                gdbDir = Directory.GetFiles(scratchDir, "a00000001.gdbtable", SearchOption.AllDirectories)
                    .Select(f => Path.GetDirectoryName(f))
                    .FirstOrDefault();
            }

            if (gdbDir == null)
            {
                throw new InvalidDataException("Archive does not contain a valid File Geodatabase (.gdb directory).");
            }

            return new FileGdbScratch(scratchDir, gdbDir);
        }
        catch (InvalidDataException)
        {
            CleanupFileGdbScratchDirectory(scratchDir);
            throw;
        }
        catch
        {
            CleanupFileGdbScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // Best-effort cleanup; scratch directory deletion will handle this
                }
            }
        }
    }

    private static ShapefileEntries? SelectShapefileEntries(ZipArchive archive)
    {
        var groups = new Dictionary<string, ShapefileEntryGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(extension) || !_shapefileComponentExtensions.Contains(extension))
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                continue;
            }

            var groupKey = GetShapefileComponentGroupKey(entry, baseName);
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new ShapefileEntryGroup(baseName);
                groups.Add(groupKey, group);
            }

            switch (extension.ToLowerInvariant())
            {
                case ".shp":
                    group.Shp = entry;
                    break;
                case ".dbf":
                    group.Dbf = entry;
                    break;
                case ".shx":
                    group.Shx = entry;
                    break;
                case ".prj":
                    group.Prj = entry;
                    break;
                case ".cpg":
                    group.Cpg = entry;
                    break;
            }
        }

        foreach (var group in groups.Values)
        {
            if (group.Shp != null && group.Dbf != null)
            {
                return new ShapefileEntries(group.BaseName, group.Shp, group.Dbf, group.Shx, group.Prj, group.Cpg);
            }
        }

        return null;
    }

    private static string GetShapefileComponentGroupKey(ZipArchiveEntry entry, string baseName)
    {
        var normalizedName = entry.FullName.Replace('\\', '/');
        var slashIndex = normalizedName.LastIndexOf('/');
        var directory = slashIndex >= 0 ? normalizedName[..slashIndex] : string.Empty;
        return string.Concat(directory, "/", baseName);
    }

    private static ZipArchiveEntry? SelectKmzKmlEntry(ZipArchive archive)
    {
        ZipArchiveEntry? firstMatch = null;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(entry.Name), ".kml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(entry.Name, "doc.kml", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            firstMatch ??= entry;
        }

        return firstMatch;
    }

    private ArchiveExtractionBudget CreateArchiveExtractionBudget()
    {
        var maxEntryBytes = _limits.MaxArchiveEntryBytes > 0
            ? _limits.MaxArchiveEntryBytes
            : DefaultMaxArchiveEntryBytes;
        var maxTotalBytes = _limits.MaxArchiveExtractedBytes > 0
            ? _limits.MaxArchiveExtractedBytes
            : DefaultMaxArchiveExtractedBytes;
        var maxCompressionRatio = _limits.MaxArchiveCompressionRatio > 1
            ? _limits.MaxArchiveCompressionRatio
            : DefaultMaxArchiveCompressionRatio;

        if (maxTotalBytes < maxEntryBytes)
        {
            maxTotalBytes = maxEntryBytes;
        }

        return new ArchiveExtractionBudget(maxTotalBytes, maxEntryBytes, maxCompressionRatio);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        ArchiveExtractionBudget extractionBudget,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.CompressedLength < 0)
        {
            throw new InvalidDataException($"Archive entry '{entry.FullName}' has an invalid size.");
        }

        if (entry.Length > extractionBudget.MaxEntryBytes)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullName}' exceeds maximum uncompressed size ({extractionBudget.MaxEntryBytes:N0} bytes).");
        }

        if (entry.Length > extractionBudget.MaxTotalBytes - extractionBudget.TotalExtractedBytes)
        {
            throw new InvalidDataException(
                $"Archive extraction exceeds maximum total uncompressed size ({extractionBudget.MaxTotalBytes:N0} bytes).");
        }

        if (entry.Length > 0)
        {
            if (entry.CompressedLength <= 0)
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' has invalid compressed size.");
            }

            var compressionRatio = (double)entry.Length / entry.CompressedLength;
            if (compressionRatio > extractionBudget.MaxCompressionRatio)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' exceeds maximum compression ratio ({extractionBudget.MaxCompressionRatio:N0}).");
            }
        }

        var entryExtractedBytes = 0L;
        var buffer = new byte[64 * 1024];
        await using var entryStream = entry.Open();
        await using var outputStream = File.Create(destinationPath);
        while (true)
        {
            var bytesRead = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            entryExtractedBytes += bytesRead;
            if (entryExtractedBytes > extractionBudget.MaxEntryBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' exceeds maximum uncompressed size ({extractionBudget.MaxEntryBytes:N0} bytes).");
            }

            if (bytesRead > extractionBudget.MaxTotalBytes - extractionBudget.TotalExtractedBytes)
            {
                throw new InvalidDataException(
                    $"Archive extraction exceeds maximum total uncompressed size ({extractionBudget.MaxTotalBytes:N0} bytes).");
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            extractionBudget.TotalExtractedBytes += bytesRead;
        }
    }
}
