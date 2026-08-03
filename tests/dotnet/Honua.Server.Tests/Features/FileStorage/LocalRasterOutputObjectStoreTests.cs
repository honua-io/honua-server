// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

public sealed class LocalRasterOutputObjectStoreTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.Parse("2026-08-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddDays(1);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "honua-server-raster-output-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ListExpiredAsync_RestrictsEnumerationAndRecoversStagedLease()
    {
        var payload = new byte[256];
        RandomNumberGenerator.Fill(payload);
        var stage = Stage(payload);
        var store = CreateStore();
        await using (var content = new MemoryStream(payload, writable: false))
        {
            await store.StageAsync(stage, content);
        }

        var stagePath = PathFor(stage.ObjectKey);
        File.SetLastWriteTimeUtc(stagePath, CreatedAt.UtcDateTime);
        var unrelatedPath = Path.Combine(_root, "uploads", "unrelated.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedPath)!);
        await File.WriteAllBytesAsync(unrelatedPath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(unrelatedPath, CreatedAt.UtcDateTime);

        var candidates = new List<RasterStoredObject>();
        await foreach (var candidate in store.ListExpiredAsync(ExpiresAt, 10))
        {
            candidates.Add(candidate);
        }

        candidates.Should().ContainSingle();
        candidates[0].ObjectKey.Should().Be(stage.ObjectKey);
        candidates[0].ExpiresAt.Should().Be(stage.ExpiresAt);
        File.Exists(unrelatedPath).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalRasterOutputObjectStore CreateStore() => new(
        Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            LocalStorage = new LocalStorageOptions
            {
                BasePath = _root,
                CreateDirectoryIfNotExists = true
            }
        }),
        Options.Create(new RasterOutputPublicationOptions { StoreReference = "gp-results" }));

    private static StagedRasterOutputDescriptor Stage(byte[] payload)
    {
        const string jobId = "job-local-store";
        const int attempt = 1;
        const string outputName = "elevation";
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return new StagedRasterOutputDescriptor
        {
            JobId = jobId,
            Attempt = attempt,
            OutputName = outputName,
            StoreReference = "gp-results",
            ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey(jobId, attempt, outputName),
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
                Width = 16,
                Height = 16,
                BandCount = 1,
                GeoTransform = [0, 1, 0, 16, 0, -1]
            },
            Engine = new RasterProducingEngine("gdal", "3.11.0"),
            Lineage = new RasterOutputLineage
            {
                JobId = jobId,
                Attempt = attempt,
                ProcessId = "raster.reproject",
                SourceArtifactIds = ["source-1"]
            },
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt
        };
    }

    private string PathFor(string objectKey) =>
        Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar));
}
