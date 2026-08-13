// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Idempotency contract of the post-success COG-catalog registration (#3089): a
/// retried terminal callback, a reconciler replay, and a concurrent registration
/// race must all converge on exactly one catalog row per immutable staged object,
/// and unsupported or Zarr-shaped registrations fail closed.
/// </summary>
public sealed class GeoprocessingRasterOutputRegistrarTests
{
    private const string JobId = "job-reg-1";
    private const string OutputName = "output1";

    [UnitTest]
    public async Task EnsureRegistered_ReplayedTwice_CreatesExactlyOneCatalogRow()
    {
        var cogStore = new UniqueConstraintCogStore();
        var registrar = CreateRegistrar(cogStore);
        var job = CreateSucceededJob("cog-catalog:7");
        var package = GeoprocessingResultPackageFactoryProxy(job);

        var first = await registrar.EnsureRegisteredAsync(job, package, CancellationToken.None);
        var second = await registrar.EnsureRegisteredAsync(job, package, CancellationToken.None);

        cogStore.Rows.Should().HaveCount(1);
        var expectedId = cogStore.Rows.Single().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ExtractRegisteredId(first).Should().Be(expectedId);
        ExtractRegisteredId(second).Should().Be(expectedId);
    }

    [UnitTest]
    public async Task EnsureRegistered_LosesUniqueConstraintRace_ConvergesOnWinningRow()
    {
        // Simulate a concurrent replay that inserts the row between this call's
        // existence check and its insert: the unique violation must converge on the
        // winner instead of duplicating or failing.
        var cogStore = new UniqueConstraintCogStore { InsertRaceRegistration = true };
        var registrar = CreateRegistrar(cogStore);
        var job = CreateSucceededJob("cog-catalog:7");

        var package = await registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        cogStore.Rows.Should().HaveCount(1);
        ExtractRegisteredId(package).Should().Be(
            cogStore.Rows.Single().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task EnsureRegistered_PostgisTarget_FailsClosed()
    {
        var registrar = CreateRegistrar(new UniqueConstraintCogStore());
        var job = CreateSucceededJob("postgis:7");

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("#3098");
    }

    [UnitTest]
    public async Task EnsureRegistered_ZarrShapedObject_FailsClosedAndInsertsNothing()
    {
        var cogStore = new UniqueConstraintCogStore();
        var registrar = CreateRegistrar(cogStore);
        var job = CreateSucceededJob("cog-catalog:7", objectKey: $"gp/outputs/{JobId}/a1/{OutputName}/result.zarr");

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("#3103");
        cogStore.Rows.Should().BeEmpty();
    }

    [UnitTest]
    public async Task EnsureRegistered_LocalStagedObject_FailsClosedAndInsertsNothing()
    {
        var cogStore = new UniqueConstraintCogStore();
        var registrar = CreateRegistrar(cogStore);
        var job = CreateSucceededJob("cog-catalog:7", provider: CloudStorageProvider.Local);

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("cannot be served by the cloud COG catalog");
        cogStore.Rows.Should().BeEmpty();
    }

    [UnitTest]
    public async Task EnsureRegistered_MissingDescriptorForIntent_FailsClosed()
    {
        var registrar = CreateRegistrar(new UniqueConstraintCogStore());
        var job = CreateSucceededJob("cog-catalog:7") with { ArtifactReferences = Array.Empty<string>() };

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("no typed");
    }

    [UnitTest]
    public async Task EnsureRegistered_NonSucceededJob_IsNoOp()
    {
        var cogStore = new UniqueConstraintCogStore();
        var registrar = CreateRegistrar(cogStore);
        var job = CreateSucceededJob("cog-catalog:7") with { Status = ExecutionJobStatus.Cancelled };

        await registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        cogStore.Rows.Should().BeEmpty();
    }

    private static string? ExtractRegisteredId(AnalysisResultPackage package)
        => package.Artifacts
            .Select(artifact => artifact.Metadata.GetValueOrDefault(
                RasterOutputArtifactMetadata.RegisteredCatalogRasterId))
            .FirstOrDefault(value => value is not null);

    [UnitTest]
    public async Task EnsureRegistered_SetsDurableRetentionHoldOnRegisteredObject()
    {
        // The catalog row is permanent while the job record expires from Redis; the
        // retention hold is what exempts the registered object from orphan sweeping.
        var cogStore = new UniqueConstraintCogStore();
        var objectStore = new HoldRecordingOutputObjectStore();
        objectStore.Objects.Add($"gp/outputs/{JobId}/a1/{OutputName}/result.tif");
        var registrar = CreateRegistrar(cogStore, objectStore);
        var job = CreateSucceededJob("cog-catalog:7");

        await registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);
        // Replay must be idempotent for the hold as well.
        await registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        objectStore.Holds.Should().ContainSingle()
            .Which.Should().Be($"gp/outputs/{JobId}/a1/{OutputName}/result.tif");
    }

    [UnitTest]
    public async Task EnsureRegistered_StagedObjectMissingAtHoldTime_FailsClosed()
    {
        var cogStore = new UniqueConstraintCogStore();
        var objectStore = new HoldRecordingOutputObjectStore(); // object absent
        var registrar = CreateRegistrar(cogStore, objectStore);
        var job = CreateSucceededJob("cog-catalog:7");

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no longer exists");
        cogStore.Rows.Should().BeEmpty();
    }

    [UnitTest]
    public async Task EnsureRegistered_FailedCatalogWrite_ReleasesNewRetentionHold()
    {
        var cogStore = new UniqueConstraintCogStore { FailRegistration = true };
        var objectStore = new HoldRecordingOutputObjectStore();
        var objectKey = $"gp/outputs/{JobId}/a1/{OutputName}/result.tif";
        objectStore.Objects.Add(objectKey);
        var registrar = CreateRegistrar(cogStore, objectStore);
        var job = CreateSucceededJob("cog-catalog:7");

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        cogStore.Rows.Should().BeEmpty();
        objectStore.Holds.Should().NotContain(objectKey);
    }

    [UnitTest]
    public async Task EnsureRegistered_FailedCatalogWrite_PreservesExistingRetentionHold()
    {
        var cogStore = new UniqueConstraintCogStore { FailRegistration = true };
        var objectStore = new HoldRecordingOutputObjectStore();
        var objectKey = $"gp/outputs/{JobId}/a1/{OutputName}/result.tif";
        objectStore.Objects.Add(objectKey);
        objectStore.Holds.Add(objectKey);
        var registrar = CreateRegistrar(cogStore, objectStore);
        var job = CreateSucceededJob("cog-catalog:7");

        var act = () => registrar.EnsureRegisteredAsync(
            job, GeoprocessingResultPackageFactoryProxy(job), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        cogStore.Rows.Should().BeEmpty();
        objectStore.Holds.Should().Contain(objectKey);
    }

    private static GeoprocessingRasterOutputRegistrar CreateRegistrar(
        ICogStore cogStore,
        Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? outputStore = null)
    {
        if (outputStore is null)
        {
            var defaultStore = new HoldRecordingOutputObjectStore();
            defaultStore.Objects.Add($"gp/outputs/{JobId}/a1/{OutputName}/result.tif");
            outputStore = defaultStore;
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => cogStore);
        var provider = services.BuildServiceProvider();
        return new GeoprocessingRasterOutputRegistrar(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GeoprocessingRasterOutputRegistrar>.Instance,
            outputStore);
    }

    private static AnalysisResultPackage GeoprocessingResultPackageFactoryProxy(ExecutionJobRecord job)
        => AnalysisResultPackage.CreateCompleted(
            $"{job.OperationId}:v{job.Version}",
            new ResultSummary { Title = "test", Description = "test" },
            job.ArtifactReferences
                .Select((reference, index) =>
                {
                    RasterOutputJson.TryDeserialize(reference, out var descriptor);
                    return new ArtifactRef
                    {
                        ArtifactId = $"{job.OperationId}:artifact:{index + 1}",
                        Kind = ArtifactKind.Raster,
                        Label = descriptor?.OutputName ?? $"artifact{index + 1}",
                        Metadata = descriptor is null
                            ? new Dictionary<string, string>()
                            : new Dictionary<string, string>
                            {
                                [RasterOutputArtifactMetadata.OutputName] = descriptor.OutputName,
                                [RasterOutputArtifactMetadata.Staged] = "true",
                            },
                    };
                })
                .ToArray(),
            [],
            new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = [],
            });

    private static ExecutionJobRecord CreateSucceededJob(
        string registrationTarget,
        string? objectKey = null,
        CloudStorageProvider provider = CloudStorageProvider.AwsS3)
    {
        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = JobId,
            AttemptNumber = 1,
            OutputName = OutputName,
            Content = new RasterContentIdentity
            {
                SizeBytes = 1024,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('a', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = provider,
            StoreReference = "gp-outputs",
            ObjectKey = objectKey ?? $"gp/outputs/{JobId}/a1/{OutputName}/result.tif",
        };

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            ArtifactReferences = [RasterOutputJson.Serialize(descriptor)],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "raster.resample",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"honua.geoprocessing.output_registration.{OutputName}"] = registrationTarget,
                }
            }
        };
    }

    /// <summary>
    /// In-memory <see cref="ICogStore"/> double that faithfully enforces the
    /// <c>uq_cloud_raster_object</c> unique constraint the way
    /// <c>PostgresCogStore.RegisterAsync</c> surfaces it (an
    /// <see cref="InvalidOperationException"/> for a duplicate identity), including an
    /// optional simulated concurrent insert between the caller's existence check and
    /// its own insert.
    /// </summary>
    private sealed class UniqueConstraintCogStore : ICogStore
    {
        private long _nextId = 1;
        private bool _raceArmed = true;

        public List<CogRegistration> Rows { get; } = [];

        public bool InsertRaceRegistration { get; init; }

        public bool FailRegistration { get; init; }

        public Task<CogRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.FirstOrDefault(row => row.Id == id));

        public Task<CogRegistration> RegisterAsync(
            CogRegistrationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (FailRegistration)
            {
                throw new InvalidOperationException("Catalog write failed.");
            }

            if (InsertRaceRegistration && _raceArmed)
            {
                // A concurrent replay wins the insert race just before this call.
                _raceArmed = false;
                Insert(request);
            }

            if (Rows.Any(row => Matches(row, request)))
            {
                throw new InvalidOperationException(
                    $"A COG is already registered for {request.Provider}://{request.Bucket}/{request.ObjectKey}.");
            }

            return Task.FromResult(Insert(request));
        }

        public Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.RemoveAll(row => row.Id == id) > 0);

        public Task<CogRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.Where(row => row.LayerId == layerId).ToArray());

        public Task UpdateMetadataAsync(
            long id,
            CogMetadata metadata,
            byte[]? ifdCache,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private CogRegistration Insert(CogRegistrationRequest request)
        {
            var row = new CogRegistration
            {
                Id = _nextId++,
                LayerId = request.LayerId,
                Name = request.Name,
                Description = request.Description,
                Provider = request.Provider,
                Bucket = request.Bucket,
                ObjectKey = request.ObjectKey,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            Rows.Add(row);
            return row;
        }

        private static bool Matches(CogRegistration row, CogRegistrationRequest request)
            => row.LayerId == request.LayerId
               && row.Provider == request.Provider
               && string.Equals(row.Bucket, request.Bucket, StringComparison.Ordinal)
               && string.Equals(row.ObjectKey, request.ObjectKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Minimal output-store double for hold assertions: matches the descriptors'
    /// (AwsS3, gp-outputs) identity and records retention holds.
    /// </summary>
    private sealed class HoldRecordingOutputObjectStore
        : Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore
    {
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Holds { get; } = new(StringComparer.Ordinal);

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public string StoreReference => "gp-outputs";

        public Task<RasterContentIdentity> WriteAsync(
            string objectKey, Stream content, string mediaType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task<Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingStagedObjectInfo?> GetInfoAsync(
            string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingStagedObjectInfo?>(null);

        public async IAsyncEnumerable<Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingStagedObjectInfo> ListAsync(
            string keyPrefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.Remove(objectKey));

        public Task<bool> TryAcquireReadLeaseAsync(
            string objectKey, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.Contains(objectKey));

        public Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingRetentionHoldResult> SetRetentionHoldAsync(
            string objectKey, CancellationToken cancellationToken = default)
        {
            if (!Objects.Contains(objectKey))
            {
                return Task.FromResult(
                    Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingRetentionHoldResult.ObjectMissing);
            }

            var added = Holds.Add(objectKey);
            return Task.FromResult(added
                ? Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingRetentionHoldResult.Added
                : Honua.Core.Features.Geoprocessing.Abstractions.GeoprocessingRetentionHoldResult.AlreadyHeld);
        }

        public Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Holds.Contains(objectKey));

        public Task ReleaseRetentionHoldAsync(
            string objectKey, CancellationToken cancellationToken = default)
        {
            Holds.Remove(objectKey);
            return Task.CompletedTask;
        }
    }
}
