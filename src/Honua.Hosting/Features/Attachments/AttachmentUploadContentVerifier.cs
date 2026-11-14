// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Attachments;

internal sealed class AttachmentUploadContentVerifier(
    IAttachmentUploadSessionStore sessions,
    IOptions<FileUploadSecurityOptions> security)
{
    public async Task<FileStream> VerifyAsync(AttachmentUploadSession session, CancellationToken cancellationToken = default)
    {
        ValidateFilename(session.Request.Filename);
        var content = CreateStagingFile();
        try
        {
            var key = new AttachmentUploadSessionKey(session.Request.OwnerId, session.SessionId);
            await sessions.CopyContentAsync(key, content, cancellationToken).ConfigureAwait(false);
            await content.FlushAsync(cancellationToken).ConfigureAwait(false);
            await VerifyDigestAsync(content, session.Request, cancellationToken).ConfigureAwait(false);
            await ScanAsync(content.Name, session.Request, cancellationToken).ConfigureAwait(false);
            content.Position = 0;
            return content;
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static FileStream CreateStagingFile()
        => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
        });

    private static void ValidateFilename(string filename)
    {
        if (!FileUploadSecurity.ValidateFileName(filename).IsValid)
        {
            throw InvalidContent();
        }
    }

    private static async Task VerifyDigestAsync(FileStream content, AttachmentUploadRequest request, CancellationToken cancellationToken)
    {
        if (content.Length != request.ExpectedSize)
        {
            throw InvalidContent();
        }

        content.Position = 0;
        var digest = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(digest, request.Sha256))
        {
            throw InvalidContent();
        }
    }

    private async Task ScanAsync(string path, AttachmentUploadRequest request, CancellationToken cancellationToken)
    {
        var result = await FileUploadSecurity.ValidateFileContentAsync(
            request.Filename, request.ContentType, request.ExpectedSize,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            security.Value.MaxSecurityScanSizeBytes, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw InvalidContent();
        }
    }

    private static AttachmentUploadException InvalidContent()
        => new(AttachmentUploadFailure.InvalidContent, "The attachment content is invalid.");
}
