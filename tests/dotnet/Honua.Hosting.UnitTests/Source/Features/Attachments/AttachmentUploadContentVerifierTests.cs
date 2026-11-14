// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Attachments;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Attachments;

public sealed class AttachmentUploadContentVerifierTests
{
    [UnitTest]
    public async Task VerifyAsync_ValidContent_RewindsStreamAndDeletesFileOnDispose()
    {
        var bytes = Encoding.UTF8.GetBytes("field note");
        var (verifier, session) = Create(bytes);
        var content = await verifier.VerifyAsync(session);
        var path = content.Name;
        content.Position.Should().Be(0);
        using var reader = new StreamReader(content, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Be("field note");

        await content.DisposeAsync();

        File.Exists(path).Should().BeFalse();
    }

    [UnitTest]
    public async Task VerifyAsync_DigestMismatch_RejectsContent()
    {
        var (verifier, session) = Create(Encoding.UTF8.GetBytes("field note"));
        session = session with { Request = session.Request with { Sha256 = new byte[32] } };

        var action = () => verifier.VerifyAsync(session);

        (await action.Should().ThrowAsync<AttachmentUploadException>())
            .Which.Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
    }

    [UnitTest]
    public async Task VerifyAsync_SizeMismatch_RejectsContent()
    {
        var (verifier, session) = Create(Encoding.UTF8.GetBytes("field note"));
        session = session with { Request = session.Request with { ExpectedSize = 1 } };

        var action = () => verifier.VerifyAsync(session);

        (await action.Should().ThrowAsync<AttachmentUploadException>())
            .Which.Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
    }

    [UnitTest]
    public async Task VerifyAsync_DangerousTextBeyondScanPrefix_RejectsContent()
    {
        var bytes = Encoding.UTF8.GetBytes(new string(' ', 10000) + "<script>alert(1)</script>");
        var (verifier, session) = Create(bytes);

        var action = () => verifier.VerifyAsync(session);

        (await action.Should().ThrowAsync<AttachmentUploadException>())
            .Which.Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
    }

    private static (AttachmentUploadContentVerifier Verifier, AttachmentUploadSession Session) Create(byte[] content)
    {
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.CopyContentAsync(default, default!, default).ReturnsForAnyArgs(async call =>
        {
            await call.Arg<Stream>().WriteAsync(content);
        });
        var session = new AttachmentUploadSession
        {
            SessionId = Guid.NewGuid(), State = AttachmentUploadState.Uploading,
            NextOffsetBytes = content.Length, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Request = new AttachmentUploadRequest
            {
                OwnerId = "editor", ServiceId = "service", LayerId = 1, FeatureId = 2,
                Operation = "chunked", IdempotencyKey = "key", Filename = "note.txt",
                ContentType = "text/plain", ExpectedSize = content.Length, Sha256 = SHA256.HashData(content),
                MaxChunkBytes = 1024, Lifetime = TimeSpan.FromMinutes(5)
            }
        };
        return (new AttachmentUploadContentVerifier(store,
            Options.Create(new FileUploadSecurityOptions { MaxSecurityScanSizeBytes = 16 })), session);
    }
}
