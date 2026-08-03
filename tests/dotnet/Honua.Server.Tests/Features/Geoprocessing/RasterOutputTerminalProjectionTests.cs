// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class RasterOutputTerminalProjectionTests
{
    [Fact]
    public async Task SucceededJob_PersistsOutputReferencesBeforePackageAndManifestCleanup()
    {
        await using var fixture = new CallbackFixture(ExecutionJobStatus.Succeeded);
        AnalysisResultPackage? storedPackage = null;
        fixture.ResultPackageStore.SetAsync(
                fixture.Job.OperationId,
                Arg.Do<AnalysisResultPackage>(package => storedPackage = package),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fixture.Events.Add("package");
                return Task.CompletedTask;
            });

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(
            new[] { "job-cas", "package", "job-cas", "retry-complete", "manifest-delete" },
            fixture.Events);
        Assert.NotNull(fixture.PersistedCandidate);
        var reference = Assert.Single(fixture.PersistedCandidate!.ArtifactReferences);
        Assert.True(RasterOutputArtifactReference.TryParseOutput(reference, out var output));
        Assert.NotNull(output);
        Assert.NotNull(storedPackage);
        Assert.Equal(fixture.Job.Version + 1, fixture.PersistedCandidate.Version);
        Assert.Equal(
            fixture.Job.OperationId + ":v" + (fixture.Job.Version + 2).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            storedPackage!.ResultPackageId);
    }

    [Fact]
    public async Task PackageFailure_RetainsManifestForIdempotentReplay()
    {
        await using var fixture = new CallbackFixture(ExecutionJobStatus.Succeeded);
        fixture.ResultPackageStore.SetAsync(
                Arg.Any<string>(),
                Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fixture.Events.Add("package-failed");
                return Task.FromException(new InvalidOperationException("simulated package outage"));
            });

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(new[] { "job-cas", "package-failed" }, fixture.Events);
        Assert.Contains(
            fixture.DurableJob.ArtifactReferences,
            reference => RasterOutputArtifactReference.TryParseManifest(reference, out _, out _));
        await fixture.ObjectStore.DidNotReceive().DeleteAsync(
            fixture.Stage.StoreReference,
            fixture.ManifestKey,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PackageFailure_IsReplayedFromDurableMarkerUntilProjectionCompletes()
    {
        await using var fixture = new CallbackFixture(ExecutionJobStatus.Succeeded);
        var packageAttempts = 0;
        fixture.ResultPackageStore.SetAsync(
                Arg.Any<string>(),
                Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                packageAttempts++;
                fixture.Events.Add(packageAttempts == 1 ? "package-failed" : "package");
                return packageAttempts == 1
                    ? Task.FromException(new InvalidOperationException("simulated package outage"))
                    : Task.CompletedTask;
            });

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);
        await fixture.Callback.OnTerminalAsync(fixture.DurableJob, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "job-cas", "package-failed", "package", "job-cas", "retry-complete", "manifest-delete"
            },
            fixture.Events);
        Assert.DoesNotContain(
            fixture.DurableJob.ArtifactReferences,
            reference => RasterOutputArtifactReference.TryParseManifest(reference, out _, out _));
        Assert.Contains(
            fixture.DurableJob.ArtifactReferences,
            reference => RasterOutputArtifactReference.TryParseOutput(reference, out _));
    }

    [Fact]
    public async Task CasFailure_RetainsManifestAndDoesNotPublishResultPackage()
    {
        await using var fixture = new CallbackFixture(
            ExecutionJobStatus.Succeeded,
            casSucceeds: false);

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(3, fixture.Events.Count(entry => entry == "job-cas"));
        Assert.DoesNotContain("manifest-delete", fixture.Events);
        await fixture.ResultPackageStore.DidNotReceiveWithAnyArgs().SetAsync(
            default!,
            default!,
            default,
            default);
        await fixture.ObjectStore.DidNotReceive().DeleteAsync(
            fixture.Stage.StoreReference,
            fixture.ManifestKey,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CasProjection_PreservesConcurrentlyPersistedNonRasterArtifact()
    {
        const string concurrentArtifact = "urn:honua:analysis:concurrent-result";
        await using var fixture = new CallbackFixture(
            ExecutionJobStatus.Succeeded,
            additionalDurableArtifactReference: concurrentArtifact);

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);

        Assert.NotNull(fixture.PersistedCandidate);
        Assert.Contains(concurrentArtifact, fixture.PersistedCandidate!.ArtifactReferences);
        Assert.Contains(
            fixture.PersistedCandidate.ArtifactReferences,
            reference => RasterOutputArtifactReference.TryParseOutput(reference, out _));
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled)]
    public async Task NonSuccessfulJob_RemovesDurableMarkerBeforeManifestCleanup(
        ExecutionJobStatus status)
    {
        await using var fixture = new CallbackFixture(status);
        fixture.ResultPackageStore.SetAsync(
                Arg.Any<string>(),
                Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fixture.Events.Add("package");
                return Task.CompletedTask;
            });

        await fixture.Callback.OnTerminalAsync(fixture.Job, CancellationToken.None);

        Assert.NotNull(fixture.PersistedCandidate);
        Assert.Empty(fixture.PersistedCandidate!.ArtifactReferences);
        Assert.Contains("job-cas", fixture.Events);
        Assert.Contains("manifest-delete", fixture.Events);
        Assert.True(
            fixture.Events.IndexOf("job-cas") < fixture.Events.IndexOf("manifest-delete"),
            "the durable marker must be removed before its replay manifest is deleted");
    }

    private sealed class CallbackFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public CallbackFixture(
            ExecutionJobStatus status,
            bool casSucceeds = true,
            string? additionalDurableArtifactReference = null)
        {
            Stage = CreateStage();
            ManifestKey = RasterOutputWorkerContract.BuildManifestObjectKey(Stage.JobId, Stage.Attempt);
            Job = CreateJob(status, ManifestKey);
            ObjectStore = Substitute.For<IRasterOutputObjectStore>();
            var manifestStore = Substitute.For<IRasterOutputManifestStore>();
            var registry = Substitute.For<IRasterOutputRegistry>();
            var executionJobStore = Substitute.For<IExecutionJobStore, ITerminalProjectionRetryStore>();
            ResultPackageStore = Substitute.For<IGeoprocessingResultPackageStore>();
            var progressStore = Substitute.For<IUniversalProgressStore>();
            var processCatalog = Substitute.For<IProcessCatalog>();

            manifestStore.ReadManifestAsync(
                    Stage.StoreReference,
                    ManifestKey,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<RasterOutputPublicationManifest?>(new()
                {
                    JobId = Stage.JobId,
                    Attempt = Stage.Attempt,
                    CreatedAt = Stage.CreatedAt,
                    Outputs = [Stage]
                }));
            // NSubstitute consumes the first ValueTask only to identify the configured call;
            // the callback returns a fresh instance for every runtime invocation.
#pragma warning disable CA2012
            SubstituteExtensions.Returns(
                registry.AcquireObjectLeaseAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()),
                _ => ValueTask.FromResult<IAsyncDisposable>(NoopLease.Instance));
#pragma warning restore CA2012
            ObjectStore.PublishAsync(
                    Arg.Any<RasterObjectPublicationRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<RasterObjectPublicationRequest>();
                    return Task.FromResult(new RasterStoredObject
                    {
                        StoreReference = request.Stage.StoreReference,
                        ObjectKey = request.DestinationObjectKey,
                        ObjectVersion = "version-1",
                        Content = request.Stage.Content,
                        State = RasterStoredObjectState.Published,
                        LastModifiedAt = request.PublishedAt
                    });
                });
            registry.RegisterAtomicallyAsync(
                    Arg.Any<RasterOutputRegistrationCommand>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<RasterOutputRegistrationCommand>();
                    return Task.FromResult(new RasterOutputRegistrationResult(
                        command.PublishedObject,
                        AlreadyRegistered: false));
                });
            ObjectStore.DeleteAsync(
                    Stage.StoreReference,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (string.Equals(call.ArgAt<string>(1), ManifestKey, StringComparison.Ordinal))
                    {
                        Events.Add("manifest-delete");
                    }

                    return Task.CompletedTask;
                });
            DurableJob = string.IsNullOrEmpty(additionalDurableArtifactReference)
                ? Job
                : Job with
                {
                    ArtifactReferences = Job.ArtifactReferences
                        .Concat([additionalDurableArtifactReference])
                        .ToArray()
                };
            executionJobStore.GetAsync(Job.OperationId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<ExecutionJobRecord?>(DurableJob));
            executionJobStore.TrySetAsync(
                    Arg.Any<ExecutionJobRecord>(),
                    Arg.Any<TimeSpan?>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    PersistedCandidate = call.Arg<ExecutionJobRecord>();
                    Events.Add("job-cas");
                    if (!casSucceeds || PersistedCandidate.Version != DurableJob.Version)
                    {
                        return Task.FromResult(false);
                    }

                    DurableJob = PersistedCandidate with
                    {
                        Version = checked(PersistedCandidate.Version + 1)
                    };
                    return Task.FromResult(true);
                });
            ((ITerminalProjectionRetryStore)executionJobStore)
                .CompleteTerminalProjectionAsync(Job.OperationId, Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Events.Add("retry-complete");
                    return Task.CompletedTask;
                });
            progressStore.GetProgressAsync<GeoprocessingProgress>(
                    Job.OperationId,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<GeoprocessingProgress?>(null));

            _services = new ServiceCollection()
                .AddSingleton(ObjectStore)
                .AddSingleton(manifestStore)
                .AddSingleton(registry)
                .AddSingleton(new RasterOutputPublisher(ObjectStore, registry))
                .AddSingleton<IOptionsMonitor<RasterOutputPublicationOptions>>(
                    new StaticOptionsMonitor<RasterOutputPublicationOptions>(new RasterOutputPublicationOptions()))
                .BuildServiceProvider();
            Callback = new GeoprocessingJobTerminalCallback(
                progressStore,
                processCatalog,
                new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions
                {
                    ResultRetention = TimeSpan.FromDays(7)
                }),
                ResultPackageStore,
                _services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GeoprocessingJobTerminalCallback>.Instance,
                scopedJobTokenIssuer: null,
                executionJobStore: executionJobStore);
        }

        public List<string> Events { get; } = [];
        public StagedRasterOutputDescriptor Stage { get; }
        public string ManifestKey { get; }
        public ExecutionJobRecord Job { get; }
        public IRasterOutputObjectStore ObjectStore { get; }
        public IGeoprocessingResultPackageStore ResultPackageStore { get; }
        public GeoprocessingJobTerminalCallback Callback { get; }
        public ExecutionJobRecord DurableJob { get; private set; }
        public ExecutionJobRecord? PersistedCandidate { get; private set; }

        public ValueTask DisposeAsync() => _services.DisposeAsync();

        private static StagedRasterOutputDescriptor CreateStage()
        {
            var createdAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            return new StagedRasterOutputDescriptor
            {
                JobId = "job-raster-terminal",
                Attempt = 1,
                OutputName = "result.tif",
                StoreReference = "gp-results",
                ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey(
                    "job-raster-terminal",
                    1,
                    "result.tif"),
                Content = new RasterContentIdentity
                {
                    SizeBytes = 32,
                    MediaType = "image/tiff",
                    Checksum = new RasterChecksum("sha256", new string('a', 64))
                },
                Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
                Grid = new RasterGridMetadata
                {
                    Crs = "EPSG:4326",
                    Width = 2,
                    Height = 2,
                    BandCount = 1,
                    GeoTransform = [0, 1, 0, 2, 0, -1]
                },
                Engine = new RasterProducingEngine("gdal", "3.11.0"),
                Lineage = new RasterOutputLineage
                {
                    JobId = "job-raster-terminal",
                    Attempt = 1,
                    ProcessId = "raster.reproject"
                },
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddDays(1)
            };
        }

        private static ExecutionJobRecord CreateJob(
            ExecutionJobStatus status,
            string manifestKey)
        {
            var now = new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);
            return new ExecutionJobRecord
            {
                OperationId = "job-raster-terminal",
                Version = 7,
                Status = status,
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now,
                CompletedAt = now,
                AttemptCount = 1,
                ArtifactReferences =
                [
                    RasterOutputArtifactReference.CreateManifest("gp-results", manifestKey)
                ],
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "raster.reproject",
                    ContractVersion = RasterOutputContract.JobContractVersion,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [RasterOutputWorkerContract.StoreReferenceParameter] = "gp-results"
                    }
                }
            };
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
