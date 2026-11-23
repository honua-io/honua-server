// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

internal sealed class LiveUploadFixture : IAsyncDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _content = new("owner content"u8.ToArray());
    private readonly Task<UploadResult> _upload;
    internal ConcurrentDictionary<string, UploadProgress> Stored { get; } = new();
    internal IUploadProgressStore Store { get; } = Substitute.For<IUploadProgressStore>();
    internal LocalFileStorage Storage { get; }
    internal string DirectoryPath { get; } = Path.Join(Path.GetTempPath(), $"honua-upload-{Guid.NewGuid():N}");
    internal Task Started => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal LiveUploadFixture()
    {
        Store.SetProgressAsync(Arg.Any<string>(), Arg.Any<UploadProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => RecordProgressAsync(call.ArgAt<UploadProgress>(1)));
        Store.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Stored.GetValueOrDefault(call.ArgAt<string>(0))));
        Storage = new LocalFileStorage(Options.Create(new LocalStorageOptions
        {
            BasePath = DirectoryPath,
            CreateDirectoryIfNotExists = true
        }), NullLogger<LocalFileStorage>.Instance, Store);
        _upload = Storage.UploadAsync(new FileUploadRequest
        {
            UploadId = "upload-a",
            TenantId = "tenant-a",
            Content = _content,
            FileName = "owner.txt",
            ContentType = "text/plain",
            SizeBytes = _content.Length
        });
    }

    private async Task RecordProgressAsync(UploadProgress progress)
    {
        Stored[progress.UploadId] = progress;
        if (progress.UploadId == "upload-a" && progress.Status == OperationStatus.Queued)
        {
            _entered.TrySetResult();
            await _resume.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _resume.TrySetResult();
        try
        {
            await _upload;
        }
        catch (OperationCanceledException)
        {
            // Cancellation of the registered live upload is an expected test outcome.
        }
        finally
        {
            _content.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
