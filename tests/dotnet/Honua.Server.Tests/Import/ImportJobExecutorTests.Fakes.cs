// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    private static ServiceProvider CreateGeoservicesProvider(IGeoservicesImportService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        if (service is ThrowingGeoservicesImportService throwingService)
        {
            services.AddSingleton(throwingService);
        }

        services.AddSingleton<IGeoservicesImportService>(sp => service);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateGeoServerProvider(ThrowingGeoServerImportService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton<IGeoServerImportService>(sp => sp.GetRequiredService<ThrowingGeoServerImportService>());
        return services.BuildServiceProvider();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds} seconds.");
    }

    private enum ThrowingBehavior
    {
        FailImmediately,
        ThrowAfterCancellation
    }

    private sealed class ThrowingGeoservicesImportService(ThrowingBehavior behavior) : IGeoservicesImportService
    {
        public int CallCount => _callCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public async Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);

            if (behavior == ThrowingBehavior.FailImmediately)
            {
                throw new InvalidOperationException("boom");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("late failure after cancellation");
            }

            throw new InvalidOperationException("unreachable");
        }

        public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
            MigrationManifestArtifact manifest,
            IReadOnlyDictionary<string, int> publishedLayerMap,
            Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CommittedCancelGeoservicesImportService(GeoservicesImportResult? outcome = null) : IGeoservicesImportService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public async Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            return outcome ?? GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                "Import was cancelled after the data was committed. Imported rows were already committed; check the target table, its publication, and any partial attachment copy before retrying.") with
            {
                NeedsReview = true,
                DataCommitted = true,
                FeatureCount = 4,
                FailedFeatures = 1,
                ServiceName = request.ServiceName
            };
        }

        public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
            MigrationManifestArtifact manifest,
            IReadOnlyDictionary<string, int> publishedLayerMap,
            Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
    }

    private sealed class SuccessfulGeoservicesImportService : IGeoservicesImportService
    {
        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new GeoservicesImportProgress
            {
                JobId = request.JobId!,
                Status = GeoservicesImportStatus.Publishing,
                FeaturesProcessed = 10,
                EstimatedTotalFeatures = 10,
                SourceServiceUrl = request.ServiceUrl,
                SourceLayerId = request.LayerId,
                SourceLayerName = "Roads",
                TableName = request.TableName,
                ServiceName = request.ServiceName,
                CurrentPhase = "Publishing imported layer",
                StartedAt = DateTimeOffset.UtcNow
            });

            return Task.FromResult(GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featureCount: 10,
                publishedLayerId: 42,
                serviceName: request.ServiceName,
                sourceLayerName: "Roads"));
        }

        public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
            MigrationManifestArtifact manifest,
            IReadOnlyDictionary<string, int> publishedLayerMap,
            Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
    }

    private sealed class NeedsReviewGeoservicesImportService : IGeoservicesImportService
    {
        public const string ReviewReason = "Migration is not full fidelity: 1 blocking difference(s).";

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GeoservicesImportResult.CreateNeedsReview(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featureCount: 8,
                failedFeatures: 2,
                publishedLayerId: 77,
                serviceName: request.ServiceName,
                sourceLayerName: "Hydrants",
                duration: TimeSpan.FromSeconds(1),
                warnings: [],
                attachmentCount: 0,
                failedAttachments: 0,
                reconciliationArtifact: null,
                catalogReconciliationReport: null,
                reviewReason: ReviewReason,
                fidelityVerdict: MigrationFidelityVerdicts.Incomplete,
                fidelityDifferences:
                [
                    new MigrationFidelityDifference
                    {
                        Code = MigrationFidelityDifferenceCodes.RecordsLost,
                        Severity = MigrationFidelityDifferenceSeverities.Blocking,
                        Subject = "Hydrants",
                        Expected = "0 dropped source records",
                        Actual = "2 dropped source records",
                        Summary = "2 source record(s) were read but did not land in the target table."
                    }
                ]));

        public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
            MigrationManifestArtifact manifest,
            IReadOnlyDictionary<string, int> publishedLayerMap,
            Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
    }

    private sealed class ThrowingGeoServerImportService(ThrowingBehavior behavior) : IGeoServerImportService
    {
        public int CallCount => _callCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportConfigurationAsync(request, progress: null, cancellationToken);

        public async Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);

            if (behavior == ThrowingBehavior.FailImmediately)
            {
                throw new InvalidOperationException("boom");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("late failure after cancellation");
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = nameof(ImportJobExecutorTests);

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

}
