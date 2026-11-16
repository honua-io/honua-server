// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
using Honua.Core.Features.Security;

namespace Honua.Import.FileImport;

internal static class MultipartParsingHelpers
{
    public static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition)
        => contentDisposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) &&
           (!StringSegment.IsNullOrEmpty(contentDisposition.FileName) ||
            !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar));

    public static bool HasFormDataContentDisposition(ContentDispositionHeaderValue contentDisposition)
        => contentDisposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) &&
           StringSegment.IsNullOrEmpty(contentDisposition.FileName) &&
           StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar);

    public static void TryDeleteFile(string? path, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        // Intentionally generic: this is best-effort cleanup of a staged temp file; any
        // failure (locked file, permissions, already gone) must not fail the caller.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger != null)
            {
                MultipartParsingHelpersLog.StagedFileCleanupFailed(logger, path, ex);
            }
        }
    }

    public const int MaxFormFieldBytes = 64 * 1024;

    public static Task<string> ReadFormFieldStringAsync(MultipartSection section, CancellationToken cancellationToken)
        => ReadBoundedSectionStringAsync(section, MaxFormFieldBytes, cancellationToken, "Form field");

    public static string? ResolveFileName(ContentDispositionHeaderValue contentDisposition)
    {
        var fileNameStar = HeaderUtilities.RemoveQuotes(contentDisposition.FileNameStar).Value;
        if (!string.IsNullOrWhiteSpace(fileNameStar))
        {
            return fileNameStar;
        }

        return HeaderUtilities.RemoveQuotes(contentDisposition.FileName).Value;
    }

    public static async Task<string> ReadBoundedSectionStringAsync(
        MultipartSection section,
        int maxBytes,
        CancellationToken cancellationToken,
        string sectionDescription = "Section")
    {
        using var ms = new MemoryStream(Math.Min(maxBytes, 4096));
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await section.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            if (ms.Length + bytesRead > maxBytes)
            {
                throw new InvalidDataException($"{sectionDescription} exceeds maximum allowed size of {maxBytes:N0} bytes.");
            }

            ms.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static async Task<long> CopyStreamToTempFileAsync(
        Stream sourceStream,
        string tempFilePath,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        var totalBytes = 0L;
        PrivateTempFile.CreateDirectory(Path.GetDirectoryName(tempFilePath)!);
        await using var targetStream = PrivateTempFile.Create(
            tempFilePath,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            bufferSize: 64 * 1024);

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
