// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Verifies the staged-output publication seam (#3089): with a registered output
/// store, large outputs are streamed to attempt-scoped immutable keys and published
/// as typed references (no payload bytes in the durable reference); small outputs
/// stay bounded inline; without a store the legacy data-URI publication and its
/// <c>MaxArtifactBytes</c> ceiling are preserved; Zarr-shaped outputs fail closed.
/// </summary>
public sealed class GdalArtifactPublisherTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("honua-gdal-publish-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    [Fact]
    public async Task PublishFileAsync_LargeOutputWithStore_PublishesStagedReference()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 1024);
        var outputPath = WriteOutput("result.tif", size: 64 * 1024);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff; application=geotiff", "Output raster", CancellationToken.None);

        error.Should().BeNull();
        var recorded = Inner(context).Artifacts.Should().ContainSingle().Subject;
        RasterOutputJson.TryDeserialize(recorded, out var descriptor).Should().BeTrue();
        var staged = descriptor.Should().BeOfType<StagedObjectRasterOutputDescriptor>().Subject;
        staged.JobId.Should().Be(context.Job.OperationId);
        staged.AttemptNumber.Should().Be(1);
        staged.ObjectKey.Should().StartWith($"gp/outputs/{context.Job.OperationId}/a1/");
        staged.Content.MediaType.Should().Be("image/tiff");
        staged.Content.SizeBytes.Should().Be(64 * 1024);
        staged.ProducingEngine.Should().Be(RasterOutputContract.GdalWorkerEngine);
        staged.Content.Checksum!.Value.Should().Be(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))).ToLowerInvariant());

        // The payload itself lives in the store, not in the durable reference.
        store.Objects.Should().ContainKey(staged.ObjectKey);
        recorded.Length.Should().BeLessThan(4096);
    }

    [Fact]
    public async Task PublishFileAsync_SmallOutputWithStore_PublishesBoundedInlineDescriptor()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 64 * 1024);
        var outputPath = WriteOutput("result.tif", size: 512);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().BeNull();
        var recorded = Inner(context).Artifacts.Should().ContainSingle().Subject;
        RasterOutputJson.TryDeserialize(recorded, out var descriptor).Should().BeTrue();
        descriptor.Should().BeOfType<InlineRasterOutputDescriptor>()
            .Which.Payload.Should().Equal(File.ReadAllBytes(outputPath));
        store.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishFileAsync_RetriedAttempt_StagesUnderNewAttemptScopedKey()
    {
        var (firstContext, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 16);
        var outputPath = WriteOutput("result.tif", size: 2048);

        (await GdalArtifactPublisher.PublishFileAsync(
            firstContext, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None)).Should().BeNull();

        var (retryContext, _) = CreateStagedContext(
            attemptCount: 2, maxInlineBytes: 16, store, firstContext.Job.OperationId);
        (await GdalArtifactPublisher.PublishFileAsync(
            retryContext, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None)).Should().BeNull();

        // A retried attempt cannot overwrite the objects a previous attempt staged.
        store.Objects.Keys.Should().HaveCount(2);
        store.Objects.Keys.Should().Contain(key => key.Contains("/a1/"));
        store.Objects.Keys.Should().Contain(key => key.Contains("/a2/"));
    }

    /// <summary>
    /// #3089 review: an output carrying a post-success registration intent must be
    /// staged regardless of size — only staged objects can register into the COG
    /// catalog, so an inline descriptor would deterministically fail registration on
    /// every results read and permanently wedge the succeeded job.
    /// </summary>
    [Fact]
    public async Task PublishFileAsync_SmallOutputWithRegistrationIntent_IsStagedNotInlined()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 64 * 1024);
        var jobWithIntent = context.Job with
        {
            Spec = context.Job.Spec with
            {
                Parameters = new Dictionary<string, string>(context.Job.Spec.Parameters, StringComparer.Ordinal)
                {
                    // The publisher resolves this slot's name as "output1" (no
                    // recorded output-name parameters), so the intent targets it.
                    ["honua.geoprocessing.output_registration.output1"] = "cog-catalog:7",
                }
            }
        };
        var inner = Inner(context);
        var intentContext = new GdalStagedOutputContext(inner, jobWithIntent, store, context.StagingOptions);
        var outputPath = WriteOutput("result.tif", size: 512);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            intentContext, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().BeNull();
        var recorded = inner.Artifacts.Should().ContainSingle().Subject;
        RasterOutputJson.TryDeserialize(recorded, out var descriptor).Should().BeTrue();
        descriptor.Should().BeOfType<StagedObjectRasterOutputDescriptor>();
        store.Objects.Should().ContainSingle();
    }

    /// <summary>
    /// #3089 review: the grid summary is best-effort — a malformed/truncated TIFF
    /// whose bounded header parse faults (including argument/overflow faults from the
    /// IFD parser) must degrade to a null grid, never fail the publication.
    /// </summary>
    [Fact]
    public async Task PublishFileAsync_MalformedTiff_DegradesToNullGrid()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 16);
        // A syntactically plausible little-endian TIFF header whose first IFD offset
        // points at a directory the file does not contain, so directory parsing
        // faults after the header probe accepts it.
        var bytes = new byte[64];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[3] = 0;
        bytes[4] = 56; // IFD offset near the end of the file
        // Declared entry count far larger than the remaining bytes.
        bytes[56] = 0xFF;
        bytes[57] = 0x7F;
        var outputPath = Path.Join(_scratch, Guid.NewGuid().ToString("N") + "-malformed.tif");
        File.WriteAllBytes(outputPath, bytes);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().BeNull();
        var recorded = Inner(context).Artifacts.Should().ContainSingle().Subject;
        RasterOutputJson.TryDeserialize(recorded, out var descriptor).Should().BeTrue();
        descriptor!.Grid.Should().BeNull();
        store.Objects.Should().ContainSingle();
    }

    [Fact]
    public async Task PublishFileAsync_WithoutStore_KeepsLegacyBoundedDataUri()
    {
        var context = new RecordingJobExecutionContext("job-legacy");
        var outputPath = WriteOutput("result.tif", size: 2048);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(), NullLogger.Instance, "job-legacy", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().BeNull();
        context.Artifacts.Should().ContainSingle()
            .Which.Should().StartWith("data:image/tiff;base64,");
    }

    [Fact]
    public async Task PublishFileAsync_WithoutStoreAboveCeiling_FailsWithActionableError()
    {
        var context = new RecordingJobExecutionContext("job-legacy");
        var outputPath = WriteOutput("result.tif", size: 4096);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(maxArtifactBytes: 1024), NullLogger.Instance, "job-legacy", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().NotBeNull();
        error.Should().Contain("MaxArtifactBytes");
        error.Should().Contain("OutputStaging");
        context.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishFileAsync_ZarrShapedOutputWithStore_FailsClosed()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 16);
        var outputPath = WriteOutput("result.zarr", size: 2048);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().NotBeNull();
        error.Should().Contain("#3103");
        store.Objects.Should().BeEmpty();
        Inner(context).Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishFileAsync_StagedOutputAboveStagedCeiling_Fails()
    {
        var (context, store) = CreateStagedContext(attemptCount: 1, maxInlineBytes: 16);
        var outputPath = WriteOutput("result.tif", size: 8 * 1024);

        var error = await GdalArtifactPublisher.PublishFileAsync(
            context, CreateOptions(maxStagedArtifactBytes: 4 * 1024), NullLogger.Instance, "job-1", outputPath,
            "image/tiff", "Output raster", CancellationToken.None);

        error.Should().NotBeNull();
        error.Should().Contain("MaxStagedArtifactBytes");
        store.Objects.Should().BeEmpty();
    }

    private readonly Dictionary<GdalStagedOutputContext, RecordingJobExecutionContext> _inners = [];

    private RecordingJobExecutionContext Inner(GdalStagedOutputContext context) => _inners[context];

    private (GdalStagedOutputContext Context, InMemoryOutputObjectStore Store) CreateStagedContext(
        int attemptCount,
        int maxInlineBytes,
        InMemoryOutputObjectStore? store = null,
        string? jobId = null)
    {
        store ??= new InMemoryOutputObjectStore();
        var job = GdalJobFactory.Job("raster.resample") with
        {
            AttemptCount = attemptCount,
            OperationId = jobId ?? ("job-" + Guid.NewGuid().ToString("N")),
        };
        var options = new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            MaxInlineArtifactBytes = maxInlineBytes,
        };
        var inner = new RecordingJobExecutionContext(job.OperationId);
        var context = new GdalStagedOutputContext(inner, job, store, options);
        _inners[context] = inner;
        return (context, store);
    }

    private string WriteOutput(string fileName, int size)
    {
        var path = Path.Join(_scratch, Guid.NewGuid().ToString("N") + "-" + fileName);
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static GdalWorkerOptions CreateOptions(
        long maxArtifactBytes = 50L * 1024 * 1024,
        long maxStagedArtifactBytes = 10L * 1024 * 1024 * 1024) => new()
        {
            MaxArtifactBytes = maxArtifactBytes,
            MaxStagedArtifactBytes = maxStagedArtifactBytes,
        };

    /// <summary>
    /// Deterministic in-memory <see cref="IGeoprocessingOutputObjectStore"/> double
    /// mirroring the filesystem store's contract (create-once keys, sha256 identity).
    /// </summary>
    internal sealed class InMemoryOutputObjectStore : IGeoprocessingOutputObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ReadLeases { get; } = new(StringComparer.Ordinal);

        public CloudStorageProvider Provider => CloudStorageProvider.Local;

        public string StoreReference => "gp-outputs";

        public async Task<RasterContentIdentity> WriteAsync(
            string objectKey,
            Stream content,
            string mediaType,
            CancellationToken cancellationToken = default)
        {
            if (Objects.ContainsKey(objectKey))
            {
                throw new InvalidOperationException($"Object '{objectKey}' already exists.");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            Objects[objectKey] = bytes;
            return new RasterContentIdentity
            {
                SizeBytes = bytes.Length,
                MediaType = mediaType,
                Checksum = new RasterChecksum(
                    "sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            };
        }

        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(Objects.TryGetValue(objectKey, out var bytes)
                ? new Honua.TestKit.CallerOwnedMemoryStream(bytes, writable: false)
                : null);

        public Task<GeoprocessingStagedObjectInfo?> GetInfoAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.TryGetValue(objectKey, out var bytes)
                ? new GeoprocessingStagedObjectInfo(objectKey, bytes.Length, DateTimeOffset.UtcNow)
                : null);

        public async IAsyncEnumerable<GeoprocessingStagedObjectInfo> ListAsync(
            string keyPrefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (key, bytes) in Objects)
            {
                if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
                {
                    yield return new GeoprocessingStagedObjectInfo(key, bytes.Length, DateTimeOffset.UtcNow);
                }
            }

            await Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.Remove(objectKey));

        public Task<bool> TryAcquireReadLeaseAsync(
            string objectKey,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            if (!Objects.ContainsKey(objectKey))
            {
                return Task.FromResult(false);
            }

            ReadLeases.Add(objectKey);
            return Task.FromResult(true);
        }

        public Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(ReadLeases.Contains(objectKey));

        public Task<bool> SetRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (!Objects.ContainsKey(objectKey))
            {
                return Task.FromResult(false);
            }

            RetentionHolds.Add(objectKey);
            return Task.FromResult(true);
        }

        public Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(RetentionHolds.Contains(objectKey));

        public HashSet<string> RetentionHolds { get; } = new(StringComparer.Ordinal);
    }
}
