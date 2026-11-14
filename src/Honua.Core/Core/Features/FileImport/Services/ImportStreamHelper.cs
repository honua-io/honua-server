// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Security;
namespace Honua.Core.Features.FileImport.Services;

internal static class ImportStreamHelper
{
    internal static async Task<FileStream> SpillToSeekableTempAsync(Stream source, CancellationToken cancellationToken)
    {
        // The name is a fixed prefix plus a fresh GUID, created exclusively and owner-only.
        var tempFile = new FileStream(
            PrivateTempFile.NewPath("honua-import-", ".tmp"),
            PrivateTempFile.ApplyPrivateCreateNew(new FileStreamOptions
            {
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
            }));
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
