// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.FileStorage;

// Real filesystem/provider behavior with controlled admission. These cases do not
// qualify Redis initialization, cross-process flock, or recovery census.
public sealed class FencedLocalFileStorageTests
{
    [UnitTest]
    public async Task SeparateProviderInstancesReadPublishedMetadataAndCannotOverwriteImmutableContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var first = fixture.Storage();
        var other = fixture.Storage(); // Deliberately initialize its index before publication.
        var result = await first.UploadAsync(Request("a/b", [1, 2, 3], 3));
        Assert.True(result.Success);
        Assert.True(await other.ExistsAsync("a/b"));
        Assert.Equal("a/b", Assert.Single(await other.ListFilesAsync()).FileId);
        Assert.Equal(result.File!.ETag, (await other.GetMetadataAsync("a/b"))!.ETag);
        Assert.False((await other.UploadAsync(Request("a/b", [9, 9, 9], 3))).Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Join(fixture.Root, "a", "b")));
        Assert.False(await other.DeleteIfMatchAsync("a/b", "stale"));
        Assert.True(await other.DeleteIfMatchAsync("a/b", result.File.ETag!));
        Assert.False(await first.ExistsAsync("a/b"));
        Assert.Empty(await first.ListFilesAsync());
        Assert.False(File.Exists(Path.Join(fixture.Root, "a", "b")));
        Assert.Equal(0, fixture.Fence.ActiveObjects);
    }

    [UnitTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ActualContentMustMatchReservedLengthBeforeMetadataPublication(int actualBytes)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var provider = fixture.Storage();
        var result = await provider.UploadAsync(Request("bounded", new byte[actualBytes], 3));
        Assert.False(result.Success);
        Assert.Null(await provider.GetMetadataAsync("bounded"));
        Assert.InRange(new FileInfo(Path.Join(fixture.Root, "bounded")).Length, 0, 3);
        Assert.Empty(Directory.GetFiles(Path.Join(fixture.Root, ".metadata"), "*.json"));
        Assert.Equal(0, fixture.Fence.ActiveObjects);
    }

    [UnitTest]
    public async Task UnreservedWriteDoesNotReadSourceOrCreateObject()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        fixture.Fence.Allow = false;
        var provider = fixture.Storage();
        var source = new UnreadableStream();
        Assert.False((await provider.UploadAsync(Request("denied", [], 3) with { Content = source })).Success);
        Assert.False(source.WasRead);
        Assert.False(File.Exists(Path.Join(fixture.Root, "denied")));
        Assert.Null(await provider.GetMetadataAsync("denied"));
    }

    [UnitTest]
    public async Task DistinctObjectKeysCannotCollideInMetadataFilename()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var provider = fixture.Storage();
        var nested = await provider.UploadAsync(Request("a/b", [1], 1));
        var flat = await provider.UploadAsync(Request("a_b", [2], 1));
        Assert.True(nested.Success);
        Assert.True(flat.Success);
        var restarted = fixture.Storage();
        Assert.Equal(nested.File!.ETag, (await restarted.GetMetadataAsync("a/b"))!.ETag);
        Assert.Equal(flat.File!.ETag, (await restarted.GetMetadataAsync("a_b"))!.ETag);
        Assert.Equal(2, Directory.GetFiles(Path.Join(fixture.Root, ".metadata"), "*.json").Length);
    }

    private static FileUploadRequest Request(string key, byte[] bytes, long length)
        => new()
        {
            ObjectKeyOverride = key,
            FileName = "data.bin",
            ContentType = "application/octet-stream",
            Content = new MemoryStream(bytes),
            SizeBytes = length
        };

    [UnitTheory]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData(".metadata/private")]
    [InlineData("a\\b")]
    public async Task AliasedOrPrivateObjectKeysCannotCreateFiles(string key)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var provider = fixture.Storage();
        Assert.False((await provider.UploadAsync(Request(key, [1], 1))).Success);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Fence.ActiveObjects);
    }

    [UnitTest]
    public async Task SymbolicLinkParentCannotRedirectReservedWriteOutsideDomain()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        using var outside = new Fixture();
        var provider = fixture.Storage();
        Directory.CreateDirectory(outside.Root);
        Directory.CreateSymbolicLink(Path.Join(fixture.Root, "redirect"), outside.Root);
        Assert.False((await provider.UploadAsync(Request("redirect/object", [1], 1))).Success);
        Assert.Empty(Directory.EnumerateFiles(outside.Root));
        Assert.Null(await provider.GetMetadataAsync("redirect/object"));
    }

    [UnitTest]
    public void SymbolicLinkRootIsRejectedBeforePrivateDirectoryCreation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        using var outside = new Fixture();
        Directory.CreateDirectory(outside.Root);
        Directory.CreateSymbolicLink(fixture.Root, outside.Root);
        try
        {
            Assert.Throws<IOException>(() => fixture.Storage());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Root));
        }
        finally
        {
            Directory.Delete(fixture.Root);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Join(Path.GetTempPath(), "honua-reserved-object-" + Guid.NewGuid().ToString("N"));
        internal Admission Fence { get; } = new();
        internal LocalFileStorage Storage() => new(Options.Create(new LocalStorageOptions
        { BasePath = Root, CreateDirectoryIfNotExists = true }), NullLogger<LocalFileStorage>.Instance,
            Substitute.For<IUploadProgressStore>(), Fence);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Admission : IStorageDomainObjectMutationFence
    {
        internal bool Allow = true;
        internal int ActiveObjects;
        public ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IAsyncDisposable>(new Lease(() => { }));
        public ValueTask<IAsyncDisposable> EnterObjectMutationAsync(string objectKey, long? maximumBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Allow) throw new InvalidOperationException("No matching reservation.");
            ActiveObjects++;
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(() => ActiveObjects--));
        }
        private sealed class Lease(Action release) : IAsyncDisposable
        {
            private bool _released;
            public ValueTask DisposeAsync()
            {
                if (!_released) { _released = true; release(); }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class UnreadableStream() : MemoryStream([1, 2, 3])
    {
        internal bool WasRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            throw new InvalidOperationException("Admission must precede reading content.");
        }
    }
}
