// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Contract tests for the shared-filesystem staged output store (#3089): immutable
/// create-once keys, streaming checksum identity, containment of object keys inside
/// the staging root, and read-lease behavior.
/// </summary>
public sealed class FileSystemGeoprocessingOutputObjectStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-store-tests-").FullName;
    private readonly FileSystemGeoprocessingOutputObjectStore _store;

    public FileSystemGeoprocessingOutputObjectStoreTests()
        => _store = new FileSystemGeoprocessingOutputObjectStore(Options.Create(
            new GeoprocessingOutputStagingOptions { Enabled = true, LocalRootPath = _root }));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    [UnitTest]
    public async Task Write_ComputesStreamingSha256Identity()
    {
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        await using var content = new MemoryStream(payload);
        var identity = await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");

        identity.SizeBytes.Should().Be(payload.Length);
        identity.MediaType.Should().Be("image/tiff");
        identity.Checksum!.Algorithm.Should().Be("sha256");
        identity.Checksum.Value.Should().Be(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());

        await using var readBack = await _store.OpenReadAsync("gp/outputs/job/a1/output1/result.tif");
        using var buffer = new MemoryStream();
        await readBack!.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(payload);
    }

    [UnitTest]
    public async Task Write_ExistingKey_Throws()
    {
        await using (var first = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", first, "image/tiff");
        }

        await using var second = new MemoryStream(new byte[] { 2 });
        var act = () => _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", second, "image/tiff");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("/rooted.tif")]
    [InlineData("../escape.tif")]
    [InlineData("a/../../escape.tif")]
    [InlineData("c:\\windows\\escape.tif")]
    public async Task Write_UncontainedKey_IsRejected(string objectKey)
    {
        await using var content = new MemoryStream(new byte[] { 1 });
        var act = () => _store.WriteAsync(objectKey, content, "image/tiff");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [UnitTest]
    public async Task ReadLease_ExpiresAfterDuration()
    {
        await using (var content = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");
        }

        (await _store.TryAcquireReadLeaseAsync(
            "gp/outputs/job/a1/output1/result.tif", TimeSpan.FromMilliseconds(1))).Should().BeTrue();
        await Task.Delay(50);
        (await _store.HasActiveReadLeaseAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeFalse();

        (await _store.TryAcquireReadLeaseAsync(
            "gp/outputs/job/a1/output1/result.tif", TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await _store.HasActiveReadLeaseAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeTrue();
    }

    /// <summary>
    /// #3089 review: an EXISTING lease sidecar that cannot be parsed (torn concurrent
    /// refresh) must count as ACTIVE — failing open would let the sweeper delete an
    /// object that is being read right now.
    /// </summary>
    [UnitTest]
    public async Task ReadLease_UnparsableExistingSidecar_CountsAsActive()
    {
        await using (var content = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");
        }

        File.WriteAllText(
            Path.Join(_root, "gp/outputs/job/a1/output1/result.tif.readlease"),
            "not-a-tick-count");

        (await _store.HasActiveReadLeaseAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeTrue();
    }

    [UnitTest]
    public async Task RetentionHold_RoundTrips_AndSurvivesListing()
    {
        await using (var content = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");
        }

        (await _store.HasRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeFalse();
        (await _store.SetRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif")).Should().Be(
            GeoprocessingRetentionHoldResult.Added);
        // Idempotent.
        (await _store.SetRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif")).Should().Be(
            GeoprocessingRetentionHoldResult.AlreadyHeld);
        (await _store.HasRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeTrue();

        // The hold sidecar is store bookkeeping, never a listed object.
        var listed = new List<string>();
        await foreach (var info in _store.ListAsync("gp/outputs"))
        {
            listed.Add(info.ObjectKey);
        }

        listed.Should().ContainSingle().Which.Should().Be("gp/outputs/job/a1/output1/result.tif");

        await _store.ReleaseRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif");
        (await _store.HasRetentionHoldAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeFalse();
    }

    [UnitTest]
    public async Task RetentionHold_MissingObject_ReturnsFalse()
    {
        (await _store.SetRetentionHoldAsync("gp/outputs/absent/a1/output1/result.tif")).Should().Be(
            GeoprocessingRetentionHoldResult.ObjectMissing);
    }

    [UnitTest]
    public async Task List_ExcludesLeaseSidecars()
    {
        await using (var content = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");
        }

        (await _store.TryAcquireReadLeaseAsync(
            "gp/outputs/job/a1/output1/result.tif", TimeSpan.FromMinutes(5))).Should().BeTrue();

        var listed = new List<string>();
        await foreach (var info in _store.ListAsync("gp/outputs"))
        {
            listed.Add(info.ObjectKey);
        }

        listed.Should().ContainSingle().Which.Should().Be("gp/outputs/job/a1/output1/result.tif");
    }

    [UnitTest]
    public async Task List_ReclaimsPendingWriteOlderThanSweepGrace()
    {
        var pendingPath = Path.Join(
            _root,
            "gp/outputs/abandoned/a1/output1/result.tif" + FileSystemGeoprocessingOutputObjectStore.PendingSuffix);
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        await File.WriteAllBytesAsync(pendingPath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(pendingPath, DateTime.UtcNow.AddHours(-2));

        await foreach (var _ in _store.ListAsync("gp/outputs"))
        {
        }

        File.Exists(pendingPath).Should().BeFalse();
    }

    [UnitTest]
    public async Task List_KeepsPendingWriteWithinSweepGrace()
    {
        var pendingPath = Path.Join(
            _root,
            "gp/outputs/recent/a1/output1/result.tif" + FileSystemGeoprocessingOutputObjectStore.PendingSuffix);
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        await File.WriteAllBytesAsync(pendingPath, [1, 2, 3]);

        await foreach (var _ in _store.ListAsync("gp/outputs"))
        {
        }

        File.Exists(pendingPath).Should().BeTrue();
    }

    [UnitTest]
    public async Task List_FromAnotherStoreInstance_DoesNotReclaimActivePendingWrite()
    {
        var options = Options.Create(new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            LocalRootPath = _root,
            SweepGrace = TimeSpan.Zero,
        });
        var writerStore = new FileSystemGeoprocessingOutputObjectStore(options);
        var sweeperStore = new FileSystemGeoprocessingOutputObjectStore(options);
        await using var content = new BlockingReadStream();
        var objectKey = "gp/outputs/active/a1/output1/result.tif";

        var write = writerStore.WriteAsync(objectKey, content, "image/tiff");
        await content.WaitUntilBlockedAsync();

        try
        {
            await foreach (var _ in sweeperStore.ListAsync("gp/outputs"))
            {
                // Enumeration drives pending-object reconciliation.
            }

            var activePendingPath = Path.Join(_root, objectKey.Replace('/', Path.DirectorySeparatorChar))
                + FileSystemGeoprocessingOutputObjectStore.PendingSuffix;
            File.Exists(activePendingPath).Should().BeTrue();
        }
        finally
        {
            content.Release();
        }

        var pendingPath = Path.Join(_root, objectKey.Replace('/', Path.DirectorySeparatorChar))
            + FileSystemGeoprocessingOutputObjectStore.PendingSuffix;
        await write;

        File.Exists(pendingPath).Should().BeFalse();
        File.Exists(Path.Join(_root, objectKey.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
    }

    [UnitTest]
    public async Task Delete_RemovesObjectAndLease()
    {
        await using (var content = new MemoryStream(new byte[] { 1 }))
        {
            await _store.WriteAsync("gp/outputs/job/a1/output1/result.tif", content, "image/tiff");
        }

        (await _store.TryAcquireReadLeaseAsync(
            "gp/outputs/job/a1/output1/result.tif", TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await _store.DeleteAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeTrue();

        (await _store.GetInfoAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeNull();
        (await _store.OpenReadAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeNull();
        (await _store.DeleteAsync("gp/outputs/job/a1/output1/result.tif")).Should().BeFalse();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task;

        public void Release() => _release.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                buffer.Span[0] = 42;
                return 1;
            }

            _blocked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
