// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Infrastructure.Scene;

internal sealed class ArchiveCopyContext
{
    public required Stream EntryStream { get; init; }

    public required string RelativePath { get; init; }

    public required long CompressedLength { get; init; }

    public required string DestinationPath { get; init; }

    public required long TotalBytes { get; init; }

    public required SceneTilesetArchiveImportOptions Limits { get; init; }

    public required byte[] Buffer { get; init; }
}

internal readonly record struct CompressionRatioSample(
    long UncompressedBytes,
    long CompressedBytes,
    double MaxRatio);

internal static class SceneTilesetArchiveEntryCopier
{
    internal static void ValidateEntryHeader(
        ZipArchiveEntry entry,
        string relativePath,
        SceneTilesetArchiveImportOptions limits)
    {
        if (entry.Length < 0 || entry.CompressedLength < 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' has an invalid size.");
        }

        if (entry.Length > limits.MaxSingleEntryBytes)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' exceeds the {limits.MaxSingleEntryBytes}-byte single-entry limit.");
        }

        if (entry.Length > 0)
        {
            var maxRatio = ResolveMaxRatio(limits);
            ValidateCompressionRatio(relativePath, new CompressionRatioSample(entry.Length, entry.CompressedLength, maxRatio));
        }
    }

    internal static async Task<long> CopyBoundedAsync(
        ArchiveCopyContext context,
        CancellationToken cancellationToken)
    {
        long entryBytes = 0;
        var totalBytes = context.TotalBytes;
        await using var fileStream = new FileStream(
            context.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        while (true)
        {
            var read = await context.EntryStream.ReadAsync(context.Buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            entryBytes += read;
            totalBytes += read;
            ValidateChunkBounds(context, entryBytes, totalBytes);
            await fileStream.WriteAsync(context.Buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return totalBytes;
    }

    internal static void ValidateCompressionRatio(string relativePath, CompressionRatioSample sample)
    {
        if (sample.CompressedBytes <= 0 || sample.UncompressedBytes > sample.MaxRatio * sample.CompressedBytes)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{relativePath}' exceeds the {sample.MaxRatio:N0}x compression ratio cap.");
        }
    }

    private static double ResolveMaxRatio(SceneTilesetArchiveImportOptions limits)
        => limits.MaxCompressionRatio > 1
            ? limits.MaxCompressionRatio
            : SceneTilesetArchiveImporter.DefaultMaxCompressionRatio;

    private static void ValidateChunkBounds(
        ArchiveCopyContext context,
        long entryBytes,
        long totalBytes)
    {
        var maxRatio = ResolveMaxRatio(context.Limits);
        ValidateCompressionRatio(
            context.RelativePath,
            new CompressionRatioSample(entryBytes, context.CompressedLength, maxRatio));

        if (entryBytes > context.Limits.MaxSingleEntryBytes)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive entry '{context.RelativePath}' exceeds the {context.Limits.MaxSingleEntryBytes}-byte uncompressed entry cap.");
        }

        if (totalBytes > context.Limits.MaxUncompressedBytes)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Archive exceeds the {context.Limits.MaxUncompressedBytes}-byte uncompressed byte budget.");
        }
    }
}
