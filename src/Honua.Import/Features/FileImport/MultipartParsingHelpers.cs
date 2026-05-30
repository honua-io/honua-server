// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Import.FileImport;

/// <summary>
/// Shared helpers for multipart form-data parsing and file staging cleanup.
/// </summary>
internal static class MultipartParsingHelpers
{
    /// <summary>
    /// Returns true if the content disposition represents a file upload section.
    /// </summary>
    public static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition)
        => contentDisposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) &&
           (!StringSegment.IsNullOrEmpty(contentDisposition.FileName) ||
            !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar));

    /// <summary>
    /// Returns true if the content disposition represents a plain form-data field.
    /// </summary>
    public static bool HasFormDataContentDisposition(ContentDispositionHeaderValue contentDisposition)
        => contentDisposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) &&
           StringSegment.IsNullOrEmpty(contentDisposition.FileName) &&
           StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar);

    /// <summary>
    /// Best-effort deletion of a staged temporary file.
    /// Failures are logged at Debug when a logger is supplied so that
    /// dangling uploads and permission errors remain diagnosable.
    /// </summary>
    public static void TryDeleteFile(string? path, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (Exception ex)
        {
            if (logger != null)
            {
                MultipartParsingHelpersLog.StagedFileCleanupFailed(logger, path, ex);
            }
        }
    }

    /// <summary>
    /// Reads the content of a multipart section as a UTF-8 string.
    /// </summary>
    public static async Task<string> ReadSectionStringAsync(MultipartSection section, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(section.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the filename from a content disposition header, preferring FileNameStar over FileName.
    /// </summary>
    public static string? ResolveFileName(ContentDispositionHeaderValue contentDisposition)
    {
        var fileNameStar = HeaderUtilities.RemoveQuotes(contentDisposition.FileNameStar).Value;
        if (!string.IsNullOrWhiteSpace(fileNameStar))
        {
            return fileNameStar;
        }

        return HeaderUtilities.RemoveQuotes(contentDisposition.FileName).Value;
    }

    /// <summary>
    /// Reads the content of a multipart section as a UTF-8 string, rejecting sections that
    /// exceed <paramref name="maxBytes"/>. Use for sidecar files (world files, .prj) where
    /// the expected payload is small and unbounded reads would be a resource risk.
    /// </summary>
    public static async Task<string> ReadBoundedSectionStringAsync(
        MultipartSection section, int maxBytes, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream(Math.Min(maxBytes, 4096));
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await section.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            if (ms.Length + bytesRead > maxBytes)
            {
                throw new InvalidDataException($"Sidecar file exceeds maximum allowed size of {maxBytes:N0} bytes.");
            }

            ms.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Copies a source stream to a temporary file with a maximum size limit.
    /// Creates the parent directory if needed. The caller is responsible for cleanup on failure.
    /// </summary>
    public static async Task<long> CopyStreamToTempFileAsync(
        Stream sourceStream,
        string tempFilePath,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        var totalBytes = 0L;
        Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath)!);
        await using var targetStream = new FileStream(tempFilePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxFileSizeBytes)
            {
                throw new InvalidDataException($"File size exceeds maximum allowed size of {maxFileSizeBytes:N0} bytes.");
            }

            await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return totalBytes;
    }
}
