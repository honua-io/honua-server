// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>
/// Integration tests for LocalFileStorage implementation
/// </summary>
[Collection("Unit")]
public class LocalFileStorageTests : IAsyncLifetime, IDisposable
{
    private readonly string _testBasePath;
    private readonly LocalFileStorage _storage;
    private bool _disposed;

    public LocalFileStorageTests()
    {
        _testBasePath = Path.Combine(Path.GetTempPath(), $"honua-test-{Guid.NewGuid():N}");
        var options = Options.Create(new LocalStorageOptions
        {
            BasePath = _testBasePath,
            CreateDirectoryIfNotExists = true
        });
        var progressStore = Substitute.For<IUploadProgressStore>();
        _storage = new LocalFileStorage(options, NullLogger<LocalFileStorage>.Instance, progressStore);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    [UnitTest]
    public void Provider_ShouldReturnLocal()
    {
        // Assert
        _storage.Provider.Should().Be(CloudStorageProvider.Local);
    }

    [UnitTest]
    public async Task UploadAsync_WithStreamContent_ShouldCreateFile()
    {
        // Arrange
        var content = "Hello, World! This is a test file."u8.ToArray();
        using var stream = new MemoryStream(content);
        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "test.txt",
            ContentType = "text/plain",
            SizeBytes = content.Length
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.File.Should().NotBeNull();
        result.File!.FileName.Should().Be("test.txt");
        result.File.ContentType.Should().Be("text/plain");
        result.File.SizeBytes.Should().Be(content.Length);
        result.File.ContentHash.Should().NotBeNullOrEmpty();
        result.File.Provider.Should().Be(CloudStorageProvider.Local);
    }

    [UnitTest]
    public async Task UploadAsync_WithNonSeekableStreamAndNoSize_ShouldSucceed()
    {
        // Arrange
        var content = "Non-seekable stream content"u8.ToArray();
        await using var stream = new NonSeekableStream(new MemoryStream(content));
        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "non-seekable.txt",
            ContentType = "text/plain"
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.File.Should().NotBeNull();
        result.File!.SizeBytes.Should().Be(content.Length);
    }

    [UnitTest]
    public async Task UploadAsync_WithTraversalFolder_ShouldFail()
    {
        // Arrange
        var content = "Traversal test content"u8.ToArray();
        using var stream = new MemoryStream(content);
        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "test.txt",
            ContentType = "text/plain",
            SizeBytes = content.Length,
            Folder = "../escape"
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Folder");
    }

    [UnitTest]
    public async Task UploadAsync_WithAbsoluteFolder_ShouldFail()
    {
        // Arrange
        var content = "Absolute folder test content"u8.ToArray();
        using var stream = new MemoryStream(content);
        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "test.txt",
            ContentType = "text/plain",
            SizeBytes = content.Length,
            Folder = Path.Combine(Path.GetTempPath(), "honua-absolute")
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Folder");
    }

    [UnitTest]
    public async Task UploadAsync_WithByteArray_ShouldCreateFile()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("GeoJSON content here");
        var request = new ByteArrayUploadRequest
        {
            Content = content,
            FileName = "test.geojson",
            ContentType = "application/geo+json"
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.File.Should().NotBeNull();
        result.File!.FileName.Should().Be("test.geojson");
    }

    [UnitTest]
    public async Task DownloadAsync_ExistingFile_ShouldReturnContent()
    {
        // Arrange
        var originalContent = "Test content for download";
        var uploadResult = await UploadTestFileAsync(originalContent);

        // Act
        await using var stream = await _storage.DownloadAsync(uploadResult.File!.FileId);

        // Assert
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        var downloadedContent = await reader.ReadToEndAsync();
        downloadedContent.Should().Be(originalContent);
    }

    [UnitTest]
    public async Task DownloadAsync_NonExistingFile_ShouldReturnNull()
    {
        // Act
        var stream = await _storage.DownloadAsync("non-existing-file-id");

        // Assert
        stream.Should().BeNull();
    }

    [UnitTest]
    public async Task DownloadBytesAsync_ExistingFile_ShouldReturnBytes()
    {
        // Arrange
        var originalContent = "Binary content test";
        var uploadResult = await UploadTestFileAsync(originalContent);

        // Act
        var bytes = await _storage.DownloadBytesAsync(uploadResult.File!.FileId);

        // Assert
        bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(bytes!).Should().Be(originalContent);
    }

    [UnitTest]
    public async Task DeleteAsync_ExistingFile_ShouldRemoveFile()
    {
        // Arrange
        var uploadResult = await UploadTestFileAsync("To be deleted");

        // Act
        var deleted = await _storage.DeleteAsync(uploadResult.File!.FileId);

        // Assert
        deleted.Should().BeTrue();

        var exists = await _storage.ExistsAsync(uploadResult.File.FileId);
        exists.Should().BeFalse();
    }

    [UnitTest]
    public async Task DeleteAsync_NonExistingFile_ShouldReturnFalse()
    {
        // Act
        var deleted = await _storage.DeleteAsync("non-existing-id");

        // Assert
        deleted.Should().BeFalse();
    }

    [UnitTest]
    public async Task UploadBatchAsync_MultipleFiles_ShouldUploadAll()
    {
        // Arrange
        var files = new List<BatchFileItem>
        {
            CreateBatchFileItem("shapefile.shp", "application/octet-stream", "SHP content"),
            CreateBatchFileItem("shapefile.dbf", "application/octet-stream", "DBF content"),
            CreateBatchFileItem("shapefile.shx", "application/octet-stream", "SHX content")
        };

        var request = new BatchUploadRequest
        {
            Files = files,
            Folder = "shapefiles"
        };

        // Act
        var result = await _storage.UploadBatchAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.UploadedFiles.Should().HaveCount(3);
        result.FailedFiles.Should().BeEmpty();
        result.TotalFiles.Should().Be(3);
    }

    [UnitTest]
    public async Task DeleteBatchAsync_ExistingBatch_ShouldDeleteAllFiles()
    {
        // Arrange
        var files = new List<BatchFileItem>
        {
            CreateBatchFileItem("file1.txt", "text/plain", "Content 1"),
            CreateBatchFileItem("file2.txt", "text/plain", "Content 2")
        };

        var request = new BatchUploadRequest { Files = files };
        var uploadResult = await _storage.UploadBatchAsync(request);

        // Act
        var deletedCount = await _storage.DeleteBatchAsync(uploadResult.BatchId);

        // Assert
        deletedCount.Should().Be(2);

        foreach (var file in uploadResult.UploadedFiles)
        {
            var exists = await _storage.ExistsAsync(file.FileId);
            exists.Should().BeFalse();
        }
    }

    [UnitTest]
    public async Task GetMetadataAsync_ExistingFile_ShouldReturnMetadata()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("Test");
        var metadata = ImmutableDictionary<string, string>.Empty
            .Add("source", "test")
            .Add("format", "geojson");

        using var stream = new MemoryStream(content);
        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "metadata-test.json",
            ContentType = "application/json",
            Metadata = metadata
        };

        var uploadResult = await _storage.UploadAsync(request);

        // Act
        var fileMetadata = await _storage.GetMetadataAsync(uploadResult.File!.FileId);

        // Assert
        fileMetadata.Should().NotBeNull();
        fileMetadata!.FileName.Should().Be("metadata-test.json");
        fileMetadata.Metadata.Should().ContainKey("source");
        fileMetadata.Metadata["source"].Should().Be("test");
    }

    [UnitTest]
    public async Task ExistsAsync_ExistingFile_ShouldReturnTrue()
    {
        // Arrange
        var uploadResult = await UploadTestFileAsync("Test content");

        // Act
        var exists = await _storage.ExistsAsync(uploadResult.File!.FileId);

        // Assert
        exists.Should().BeTrue();
    }

    [UnitTest]
    public async Task ExistsAsync_NonExistingFile_ShouldReturnFalse()
    {
        // Act
        var exists = await _storage.ExistsAsync("non-existing-id");

        // Assert
        exists.Should().BeFalse();
    }

    [UnitTest]
    public async Task ListFilesAsync_WithFiles_ShouldReturnFilesList()
    {
        // Arrange
        await UploadTestFileAsync("File 1", "folder1");
        await UploadTestFileAsync("File 2", "folder1");
        await UploadTestFileAsync("File 3", "folder2");

        // Act
        var allFiles = await _storage.ListFilesAsync();
        var folder1Files = await _storage.ListFilesAsync("folder1");

        // Assert
        allFiles.Should().HaveCountGreaterOrEqualTo(3);
        folder1Files.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task GetPresignedUrlAsync_ExistingFile_ShouldReturnUrl()
    {
        // Arrange
        var uploadResult = await UploadTestFileAsync("Test for presigned URL");

        // Act
        var url = await _storage.GetPresignedUrlAsync(uploadResult.File!.FileId);

        // Assert
        url.Should().NotBeNullOrEmpty();
        url.Should().StartWith("file:///");
    }

    [UnitTest]
    public async Task GetPresignedUploadUrlAsync_ShouldReturnUrlAndFileId()
    {
        // Act
        var result = await _storage.GetPresignedUploadUrlAsync(
            "future-file.json",
            "application/json");

        // Assert
        result.Should().NotBeNull();
        result!.Value.Url.Should().NotBeNullOrEmpty();
        result.Value.FileId.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    public async Task UploadAsync_WithTimeToLive_ShouldSetExpiration()
    {
        // Arrange
        var ttl = TimeSpan.FromHours(1);
        var content = Encoding.UTF8.GetBytes("Temporary file");
        using var stream = new MemoryStream(content);

        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "temp.txt",
            ContentType = "text/plain",
            TimeToLive = ttl
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.File!.ExpiresAt.Should().NotBeNull();
        result.File.ExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(ttl),
            TimeSpan.FromMinutes(1));
    }

    [UnitTest]
    public async Task CleanupExpiredFilesAsync_WithExpiredFiles_ShouldRemoveThem()
    {
        // Arrange - Create a file with very short TTL
        var content = Encoding.UTF8.GetBytes("Expiring soon");
        using var stream = new MemoryStream(content);

        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "expiring.txt",
            ContentType = "text/plain",
            TimeToLive = TimeSpan.FromMilliseconds(1) // Very short TTL
        };

        var uploadResult = await _storage.UploadAsync(request);
        await Task.Delay(50); // Wait for file to expire

        // Act
        var cleanedCount = await _storage.CleanupExpiredFilesAsync();

        // Assert
        cleanedCount.Should().BeGreaterOrEqualTo(1);

        var exists = await _storage.ExistsAsync(uploadResult.File!.FileId);
        exists.Should().BeFalse();
    }

    [UnitTest]
    public async Task UploadAsync_WithFolder_ShouldOrganizeCorrectly()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("Organized file");
        using var stream = new MemoryStream(content);

        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = "organized.txt",
            ContentType = "text/plain",
            Folder = "imports/2024"
        };

        // Act
        var result = await _storage.UploadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.File!.StoragePath.Should().Contain("imports");
    }

    private async Task<UploadResult> UploadTestFileAsync(string content, string? folder = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);

        var request = new FileUploadRequest
        {
            Content = stream,
            FileName = $"test-{Guid.NewGuid():N}.txt",
            ContentType = "text/plain",
            Folder = folder
        };

        return await _storage.UploadAsync(request);
    }

    private static BatchFileItem CreateBatchFileItem(string fileName, string contentType, string content)
    {
        return new BatchFileItem
        {
            Content = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            FileName = fileName,
            ContentType = contentType
        };
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_testBasePath))
            {
                Directory.Delete(_testBasePath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
