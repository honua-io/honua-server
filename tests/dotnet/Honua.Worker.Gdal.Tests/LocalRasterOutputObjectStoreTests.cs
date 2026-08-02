// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class LocalRasterOutputObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "honua-raster-output-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StageAndPublishAsync_StreamsValidatesAndReplaysAtomicMove()
    {
        var payload = new byte[2 * 1024 * 1024 + 17];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload);
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");

        await using (var first = new MemoryStream(payload, writable: false))
        {
            var staged = await store.StageAsync(stage, first);
            staged.Content.Should().Be(stage.Content);
            staged.State.Should().Be(RasterStoredObjectState.Staged);
        }

        const string publishedKey = "raster/published/aa/rast_aaaaaaaa.tif";
        var published = await store.PublishAsync(new RasterObjectPublicationRequest
        {
            Stage = stage,
            DestinationObjectKey = publishedKey,
            PublishedAt = PublishedAt
        });
        var replay = await store.PublishAsync(new RasterObjectPublicationRequest
        {
            Stage = stage,
            DestinationObjectKey = publishedKey,
            PublishedAt = PublishedAt
        });

        replay.Should().Be(published);
        File.Exists(PathFor(stage.ObjectKey)).Should().BeFalse();
        File.Exists(PathFor(publishedKey)).Should().BeTrue();
        (await File.ReadAllBytesAsync(PathFor(publishedKey))).Should().Equal(payload);
    }

    [Fact]
    public async Task StageAsync_ChecksumMismatchLeavesNoStagedOrTemporaryBytes()
    {
        var payload = new byte[257];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload) with
        {
            Content = new RasterContentIdentity
            {
                SizeBytes = payload.LongLength,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('0', 64))
            }
        };
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");

        await using var content = new MemoryStream(payload, writable: false);
        var action = async () => await store.StageAsync(stage, content);

        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(_root).Should().BeTrue();
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task StageAsync_StopsStreamingAfterDeclaredSizeAndLeavesNoTemporaryBytes()
    {
        var payload = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload) with
        {
            Content = new RasterContentIdentity
            {
                SizeBytes = 128,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('0', 64))
            }
        };
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");
        await using var content = new MemoryStream(payload, writable: false);

        var action = async () => await store.StageAsync(stage, content);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*exceed their declared content size*");
        content.Position.Should().BeLessThan(payload.LongLength);
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ListExpiredAsync_ReturnsStagedAndPublishedCandidatesWithoutLoadingThemIntoWeb()
    {
        var payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload);
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");
        await using (var content = new MemoryStream(payload, writable: false))
        {
            await store.StageAsync(stage, content);
        }

        const string publishedKey = "raster/published/bb/rast_bbbbbbbb.tif";
        await store.PublishAsync(new RasterObjectPublicationRequest
        {
            Stage = stage,
            DestinationObjectKey = publishedKey,
            PublishedAt = PublishedAt
        });
        var retryStage = stage with
        {
            Attempt = stage.Attempt + 1,
            ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey(
                stage.JobId,
                stage.Attempt + 1,
                stage.OutputName),
            Lineage = stage.Lineage with { Attempt = stage.Attempt + 1 }
        };
        await using (var content = new MemoryStream(payload, writable: false))
        {
            await store.StageAsync(retryStage, content);
        }

        File.SetLastWriteTimeUtc(PathFor(publishedKey), PublishedAt.UtcDateTime);
        File.SetLastWriteTimeUtc(PathFor(retryStage.ObjectKey), PublishedAt.UtcDateTime);

        var candidates = new List<RasterStoredObject>();
        await foreach (var candidate in store.ListExpiredAsync(
            ExpiresAt,
            10))
        {
            candidates.Add(candidate);
        }

        candidates.Should().HaveCount(2);
        candidates.Should().Contain(candidate => candidate.ObjectKey == publishedKey
            && candidate.State == RasterStoredObjectState.Published);
        candidates.Should().Contain(candidate => candidate.ObjectKey == retryStage.ObjectKey
            && candidate.State == RasterStoredObjectState.Staged);
    }

    [Fact]
    public async Task PublisherAsync_LocalStoreRegistrationFailureReplaysSameImmutableObject()
    {
        var payload = new byte[2 * 1024 * 1024 + 31];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload);
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");
        await using (var content = new MemoryStream(payload, writable: false))
        {
            await store.StageAsync(stage, content);
        }

        var registry = new ReplayRegistry { FailNext = true };
        var publisher = new RasterOutputPublisher(store, registry);
        var request = new RasterOutputPublicationRequest
        {
            Stage = stage,
            CompletionState = RasterOutputCompletionState.Succeeded,
            RegistrationTarget = new RasterOutputRegistrationTarget(
                RasterOutputRegistrationKind.CatalogObject,
                "tenant-raster-catalog"),
            PublishedAt = PublishedAt,
            RetainUntil = ExpiresAt
        };

        var first = async () => await publisher.PublishAsync(request);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await publisher.PublishAsync(request);

        replay.State.Should().Be(RasterOutputPublicationState.Published);
        registry.Registrations.Should().ContainSingle();
        Directory.EnumerateFiles(
                Path.Combine(_root, "raster", "published"),
                "*.tif",
                SearchOption.AllDirectories)
            .Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StagedRasterOutputDescriptor Stage(byte[] payload)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return new StagedRasterOutputDescriptor
        {
            JobId = "job-42",
            Attempt = 2,
            OutputName = "elevation",
            StoreReference = "gp-results",
            ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey("job-42", 2, "elevation"),
            Content = new RasterContentIdentity
            {
                SizeBytes = payload.LongLength,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", checksum)
            },
            Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
            Grid = new RasterGridMetadata
            {
                Crs = "EPSG:4326",
                Width = 1024,
                Height = 512,
                BandCount = 1,
                GeoTransform = [0, 1, 0, 512, 0, -1]
            },
            Engine = new RasterProducingEngine("gdal", "3.11.0"),
            Lineage = new RasterOutputLineage
            {
                JobId = "job-42",
                Attempt = 2,
                ProcessId = "raster.reproject",
                SourceArtifactIds = ["source-1"]
            },
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt
        };
    }

    private string PathFor(string objectKey) => Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar));

    private sealed class ReplayRegistry : IRasterOutputRegistry
    {
        public bool FailNext { get; set; }

        public Dictionary<string, RasterOutputDescriptor> Registrations { get; } = new(StringComparer.Ordinal);

        public ValueTask<IAsyncDisposable> AcquireObjectLeaseAsync(
            string storeReference,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(NoopAsyncDisposable.Instance);
        }

        public Task<RasterOutputRegistrationResult> RegisterAtomicallyAsync(
            RasterOutputRegistrationCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated transaction rollback");
            }

            if (Registrations.TryGetValue(command.IdempotencyKey, out var existing))
            {
                return Task.FromResult(new RasterOutputRegistrationResult(existing, true));
            }

            Registrations.Add(command.IdempotencyKey, command.PublishedObject);
            return Task.FromResult<RasterOutputRegistrationResult>(
                new(command.PublishedObject, false));
        }

        public Task<bool> IsVisibleAsync(
            string storeReference,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Registrations.Values.OfType<ObjectStoreRasterOutputDescriptor>().Any(
                output => string.Equals(output.StoreReference, storeReference, StringComparison.Ordinal)
                    && string.Equals(output.ObjectKey, objectKey, StringComparison.Ordinal)));
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DateTimeOffset CreatedAt { get; } = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset PublishedAt { get; } = new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset ExpiresAt { get; } = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
}
