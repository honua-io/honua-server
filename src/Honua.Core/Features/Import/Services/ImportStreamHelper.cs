// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Shared stream utilities for the import pipeline. Used by both
/// database import services and format readers that require
/// seekable streams (e.g. <see cref="FlatGeobufFormatReader"/>).
/// </summary>
internal static class ImportStreamHelper
{
    /// <summary>
    /// Copies a non-seekable stream into a seekable temporary file (DeleteOnClose).
    /// Used for binary formats whose headers may exceed the fixed CRS-detection buffer.
    /// </summary>
    internal static async Task<FileStream> SpillToSeekableTempAsync(Stream source, CancellationToken cancellationToken)
    {
        var tempFile = new FileStream(
            Path.Combine(Path.GetTempPath(), $"honua-import-{Guid.NewGuid():N}.tmp"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
            });
        try
        {
            await source.CopyToAsync(tempFile, cancellationToken);
        }
        catch
        {
            await tempFile.DisposeAsync();
            throw;
        }

        tempFile.Position = 0;
        return tempFile;
    }
}
