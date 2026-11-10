// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class ObjectStorageGeoprocessingOutputObjectStoreTests
{
    private const string KeyPrefix = "gp/outputs";
    private static readonly byte[] _payload = Enumerable.Range(0, 200_000).Select(index => (byte)((index * 31 + 7) % 256)).ToArray();

    private readonly SharedBucket _bucket = new();
    private readonly AdjustableTimeProvider _time = new();

    [UnitTest]
    public async Task WriteAsync_StoresTheObjectAtItsKeyWithItsMediaTypeAndReturnsItsIdentity()
    {
        var key = NewKey();

        var identity = await WriteAsync(CreateStore(), key, _payload);

        identity.SizeBytes.Should().Be(_payload.Length);
        identity.MediaType.Should().Be("image/tiff");
        identity.Checksum!.Value.Should().Be(Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant());
        _bucket.Keys.Should().Equal(key);
        _bucket.ContentTypeOf(key).Should().Be("image/tiff");
    }

    [UnitTest]
    public async Task WriteAsync_FromAForwardOnlySource_StoresTheWholeOutput()
    {
        var key = NewKey();
        await using var forwardOnly = new System.IO.Compression.DeflateStream(Deflated(_payload), System.IO.Compression.CompressionMode.Decompress);

        var identity = await CreateStore().WriteAsync(key, forwardOnly, "image/tiff");

        identity.SizeBytes.Should().Be(_payload.Length);
        (await ReadAllAsync(CreateStore(), key)).Should().Equal(_payload);
    }

    [UnitTest]
    public async Task SetRetentionHoldAsync_WhenTheHoldCannotBeWritten_ThrowsInsteadOfReportingAHold()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);
        var storage = Substitute.For<Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage>();
        storage.UploadIfMatchAsync(default!, default, default)
            .ReturnsForAnyArgs(UploadResult.CreateFailure("access denied"));
        storage.ExistsAsync(default!, default).ReturnsForAnyArgs(false);
        var store = new ObjectStorageGeoprocessingOutputObjectStore(
            storage,
            Options.Create(new GeoprocessingOutputStagingOptions { Enabled = true }),
            new GeoprocessingOutputStorageKeyPrefix(string.Empty));

        var hold = () => store.SetRetentionHoldAsync(key);

        await hold.Should().ThrowAsync<IOException>();
    }

    [UnitTest]
    public async Task WriteAsync_ToAKeyThatAlreadyExists_ThrowsAndKeepsTheFirstObject()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);

        var secondWrite = () => WriteAsync(CreateStore(), key, [1, 2, 3]);

        await secondWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        (await ReadAllAsync(CreateStore(), key)).Should().Equal(_payload);
    }

    [UnitTest]
    public async Task OpenReadAsync_OnASecondHost_ReturnsTheWholeObjectWithItsLength()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);

        await using var stream = await CreateStore().OpenReadAsync(key);

        stream!.CanSeek.Should().BeTrue();
        stream.Length.Should().Be(_payload.Length);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.ToArray().Should().Equal(_payload);
    }

    [UnitTest]
    public async Task OpenReadAsync_SeekingForward_ReadsTheRequestedRange()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);
        await using var stream = await CreateStore().OpenReadAsync(key);

        stream!.Seek(150_000, SeekOrigin.Begin);
        var range = new byte[1_000];
        await stream.ReadExactlyAsync(range);

        range.Should().Equal(_payload.AsSpan(150_000, 1_000).ToArray());
        var seekBack = () => stream.Seek(0, SeekOrigin.Begin);
        seekBack.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    public async Task MissingObject_IsReportedAsMissingByEveryRead()
    {
        var store = CreateStore();
        var key = NewKey();

        (await store.OpenReadAsync(key)).Should().BeNull();
        (await store.GetInfoAsync(key)).Should().BeNull();
        (await store.TryAcquireReadLeaseAsync(key, TimeSpan.FromMinutes(15))).Should().BeFalse();
        (await store.SetRetentionHoldAsync(key)).Should().Be(GeoprocessingRetentionHoldResult.ObjectMissing);
        (await store.DeleteAsync(key)).Should().BeFalse();
        _bucket.Keys.Should().BeEmpty("a hold or lease is never left behind for an object that does not exist");
    }

    [UnitTest]
    public async Task DeleteAsync_RemovesTheObjectWithItsLeaseAndHold()
    {
        var store = CreateStore();
        var key = NewKey();
        await WriteAsync(store, key, _payload);
        await store.TryAcquireReadLeaseAsync(key, TimeSpan.FromMinutes(15));
        await store.SetRetentionHoldAsync(key);

        (await store.DeleteAsync(key)).Should().BeTrue();

        _bucket.Keys.Should().BeEmpty();
        (await CreateStore().GetInfoAsync(key)).Should().BeNull();
    }

    [UnitTest]
    public async Task ReadLease_TakenOnOneHost_IsSeenByAnotherUntilItExpires()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);

        (await CreateStore().TryAcquireReadLeaseAsync(key, TimeSpan.FromMinutes(15))).Should().BeTrue();

        (await CreateStore().HasActiveReadLeaseAsync(key)).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(16));
        (await CreateStore().HasActiveReadLeaseAsync(key)).Should().BeFalse();
    }

    [UnitTest]
    public async Task RetentionHold_IsAddedOnceSeenByEveryHostAndReleasable()
    {
        var key = NewKey();
        await WriteAsync(CreateStore(), key, _payload);

        (await CreateStore().SetRetentionHoldAsync(key)).Should().Be(GeoprocessingRetentionHoldResult.Added);
        (await CreateStore().SetRetentionHoldAsync(key)).Should().Be(GeoprocessingRetentionHoldResult.AlreadyHeld);
        (await CreateStore().HasRetentionHoldAsync(key)).Should().BeTrue();

        await CreateStore().ReleaseRetentionHoldAsync(key);

        (await CreateStore().HasRetentionHoldAsync(key)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("", "gp/outputs")]
    [InlineData("honua", "honua/gp/outputs")]
    public async Task ListAsync_ReturnsStagedObjectsUnderThePrefixWithoutLeasesOrHolds(string storageKeyPrefix, string keyPrefix)
    {
        var store = CreateStore(storageKeyPrefix);
        var first = GeoprocessingOutputObjectKeys.Build(keyPrefix, "job-a", 1, "outputRaster", "result.tif");
        var second = GeoprocessingOutputObjectKeys.Build(keyPrefix, "job-b", 1, "outputRaster", "result.tif");
        await WriteAsync(store, first, _payload);
        await WriteAsync(store, second, [1, 2, 3]);
        await store.TryAcquireReadLeaseAsync(first, TimeSpan.FromMinutes(15));
        await store.SetRetentionHoldAsync(first);
        await WriteAsync(CreateStore(storageKeyPrefix), "other/unrelated.bin", [9], "application/octet-stream");

        var listed = new List<GeoprocessingStagedObjectInfo>();
        await foreach (var info in CreateStore(storageKeyPrefix).ListAsync(keyPrefix))
        {
            listed.Add(info);
        }

        listed.Select(info => info.ObjectKey).Should().BeEquivalentTo(first, second);
        listed.Single(info => info.ObjectKey == first).SizeBytes.Should().Be(_payload.Length);
    }

    [UnitTest]
    public void Provider_AndStoreReference_ComeFromTheStorageAndTheOptions()
    {
        var store = CreateStore();

        store.Provider.Should().Be(CloudStorageProvider.AwsS3);
        store.StoreReference.Should().Be("gp-outputs");
    }

    private ObjectStorageGeoprocessingOutputObjectStore CreateStore(string storageKeyPrefix = "")
        => new(
            new SharedBucketCloudFileStorage(_bucket, CloudStorageProvider.AwsS3, storageKeyPrefix),
            Options.Create(new GeoprocessingOutputStagingOptions
            {
                Enabled = true,
                Provider = GeoprocessingOutputStagingOptions.ObjectStorageProvider
            }),
            new GeoprocessingOutputStorageKeyPrefix(storageKeyPrefix),
            _time);

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    private static MemoryStream Deflated(byte[] bytes)
    {
        var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        compressed.Position = 0;
        return compressed;
    }

    private static async Task<RasterContentIdentity> WriteAsync(
        ObjectStorageGeoprocessingOutputObjectStore store,
        string key,
        byte[] bytes,
        string mediaType = "image/tiff")
    {
        using var content = new MemoryStream(bytes);
        return await store.WriteAsync(key, content, mediaType);
    }

    private static string NewKey()
        => GeoprocessingOutputObjectKeys.Build(KeyPrefix, $"job-{Guid.NewGuid():N}", 1, "outputRaster", "result.tif");

    private static async Task<byte[]> ReadAllAsync(ObjectStorageGeoprocessingOutputObjectStore store, string key)
    {
        await using var stream = await store.OpenReadAsync(key);
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        return copy.ToArray();
    }
}
