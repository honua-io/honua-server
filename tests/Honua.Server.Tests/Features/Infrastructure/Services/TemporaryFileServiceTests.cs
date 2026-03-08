// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class TemporaryFileServiceTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"honua-temp-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreTemporaryFileAsync_ExceedingTotalStorageLimit_ThrowsLimitExceeded()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1100,
            MaxFileCount = 100,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[20],
            "image/png");

        Func<Task> act = async () => await service.StoreTemporaryFileAsync(
            new byte[20],
            "image/png");

        await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_ExceedingFileCountLimit_ThrowsLimitExceeded()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        Func<Task> act = async () => await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_RemovesExpiredFilesBeforeQuotaCheck()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png",
            expiration: TimeSpan.FromMilliseconds(10));

        await Task.Delay(50);

        Func<Task<string>> act = async () => await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_MultipleInstancesShareQuotaLimits()
    {
        var options = new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        };

        using var firstService = CreateService(options);
        using var secondService = CreateService(options);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> StoreAsync(FileSystemTemporaryFileService service)
        {
            await start.Task;

            try
            {
                await service.StoreTemporaryFileAsync(new byte[128 * 1024], "image/png");
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var firstStore = StoreAsync(firstService);
        var secondStore = StoreAsync(secondService);

        start.SetResult();
        var results = await Task.WhenAll(firstStore, secondStore);

        results.Count(result => result is null).Should().Be(1);
        results.Count(result => result is TemporaryStorageLimitExceededException).Should().Be(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
        {
            Directory.Delete(_storageDirectory, recursive: true);
        }
    }

    private FileSystemTemporaryFileService CreateService(TemporaryFileOptions options)
    {
        return new FileSystemTemporaryFileService(
            Options.Create(options),
            NullLogger<FileSystemTemporaryFileService>.Instance);
    }
}
