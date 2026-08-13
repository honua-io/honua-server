// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Result-package projection of typed raster output descriptors (#3089): staged
/// descriptors surface as metadata-rich artifacts through an authenticated route,
/// and — per the review — a descriptor-shaped reference this release cannot
/// interpret (for example a future contract version) must surface as an
/// unavailable artifact, never leaking the raw descriptor JSON (store reference,
/// object key, checksum) as the client-facing value.
/// </summary>
public sealed class GeoprocessingResultPackageFactoryOutputTests
{
    [UnitTest]
    public void Create_UnsupportedDescriptorShapedReference_DoesNotLeakDescriptorJson()
    {
        var unsupported =
            "{\"outputType\":\"staged-object\",\"outputContractVersion\":999," +
            "\"jobId\":\"job-x\",\"attemptNumber\":1,\"outputName\":\"output1\"," +
            "\"content\":{\"sizeBytes\":10,\"mediaType\":\"image/tiff\"}," +
            "\"producingEngine\":\"gdal-worker\",\"provider\":\"Local\"," +
            "\"storeReference\":\"gp-outputs\",\"objectKey\":\"gp/outputs/job-x/a1/output1/secret.tif\"}";

        var package = GeoprocessingResultPackageFactory.Create(
            CreateSucceededJob(unsupported), Substitute.For<IProcessCatalog>());

        var artifact = package.Artifacts.Should().ContainSingle().Subject;
        artifact.Uri.Should().BeNull();
        artifact.ContentType.Should().BeNull();
        artifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.Unsupported)
            .WhoseValue.Should().Be("true");
        artifact.Metadata.Should().NotContainKey(RasterOutputArtifactMetadata.Staged);
    }

    [UnitTest]
    public void Create_SupportedStagedDescriptor_UsesAuthenticatedContentRouteAsUri()
    {
        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = "job-x",
            AttemptNumber = 1,
            OutputName = "output1",
            Content = new RasterContentIdentity
            {
                SizeBytes = 10,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('a', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = Honua.Core.Features.Infrastructure.Domain.CloudStorageProvider.Local,
            StoreReference = "gp-outputs",
            ObjectKey = "gp/outputs/job-x/a1/output1/result.tif",
        };

        var package = GeoprocessingResultPackageFactory.Create(
            CreateSucceededJob(RasterOutputJson.Serialize(descriptor)), Substitute.For<IProcessCatalog>());

        var artifact = package.Artifacts.Should().ContainSingle().Subject;
        artifact.Uri.Should().Be("/api/geoprocessing/jobs/job-x/artifacts/0/content");
        artifact.ContentType.Should().Be("image/tiff");
        artifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.Staged);
        artifact.Metadata[RasterOutputArtifactMetadata.ObjectKey].Should().Be(descriptor.ObjectKey);
        artifact.Metadata[RasterOutputArtifactMetadata.ContentRoute].Should().Be(
            "/api/geoprocessing/jobs/job-x/artifacts/0/content");
    }

    [UnitTest]
    public void ProjectStagedArtifactAvailability_MismatchedStore_RemovesCanonicalUri()
    {
        var package = CreateStagedPackage();
        var store = Substitute.For<IGeoprocessingOutputObjectStore>();
        store.Provider.Returns(Honua.Core.Features.Infrastructure.Domain.CloudStorageProvider.Local);
        store.StoreReference.Returns("different-store");

        var projected = GeoprocessingJobArtifactService.ProjectStagedArtifactAvailability(
            package, "job-x", store);

        var projectedArtifact = projected.Artifacts.Should().ContainSingle().Subject;
        projectedArtifact.Uri.Should().BeNull();
        projectedArtifact.Metadata.Should().NotContainKey(RasterOutputArtifactMetadata.ContentRoute);
        projectedArtifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.Staged);

        var durableArtifact = package.Artifacts.Should().ContainSingle().Subject;
        durableArtifact.Uri.Should().NotBeNull(
            "availability projection must not mutate the durable package");
        durableArtifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.ContentRoute);
    }

    [UnitTest]
    public void ProjectStagedArtifactAvailability_MatchingStore_UsesCanonicalUri()
    {
        var durablePackage = CreateStagedPackage();
        var package = durablePackage with
        {
            Artifacts = [durablePackage.Artifacts[0] with { Uri = null }]
        };
        var store = Substitute.For<IGeoprocessingOutputObjectStore>();
        store.Provider.Returns(Honua.Core.Features.Infrastructure.Domain.CloudStorageProvider.Local);
        store.StoreReference.Returns("gp-outputs");

        var projected = GeoprocessingJobArtifactService.ProjectStagedArtifactAvailability(
            package, "job-x", store);

        var artifact = projected.Artifacts.Should().ContainSingle().Subject;
        artifact.Uri.Should().Be("/api/geoprocessing/jobs/job-x/artifacts/0/content");
        artifact.Metadata[RasterOutputArtifactMetadata.ContentRoute].Should().Be(artifact.Uri);
    }

    [UnitTest]
    public void Create_StagedDescriptorWithoutChecksum_IsUnavailable()
    {
        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = "job-x",
            AttemptNumber = 1,
            OutputName = "output1",
            Content = new RasterContentIdentity
            {
                SizeBytes = 10,
                MediaType = "image/tiff",
                Checksum = null,
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = Honua.Core.Features.Infrastructure.Domain.CloudStorageProvider.Local,
            StoreReference = "gp-outputs",
            ObjectKey = "gp/outputs/job-x/a1/output1/result.tif",
        };

        var package = GeoprocessingResultPackageFactory.Create(
            CreateSucceededJob(RasterOutputJson.Serialize(descriptor)), Substitute.For<IProcessCatalog>());

        var artifact = package.Artifacts.Should().ContainSingle().Subject;
        artifact.Uri.Should().BeNull();
        artifact.ContentType.Should().BeNull();
        artifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.Unsupported);
        artifact.Metadata.Should().NotContainKey(RasterOutputArtifactMetadata.Staged);
        artifact.Metadata.Should().NotContainKey(RasterOutputArtifactMetadata.ContentRoute);
    }

    [UnitTest]
    public void Create_InlineDescriptorWithMismatchedChecksum_IsUnavailable()
    {
        var descriptor = new InlineRasterOutputDescriptor
        {
            JobId = "job-x",
            AttemptNumber = 1,
            OutputName = "output1",
            Payload = [1, 2, 3],
            Content = new RasterContentIdentity
            {
                SizeBytes = 3,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('0', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
        };

        var package = GeoprocessingResultPackageFactory.Create(
            CreateSucceededJob(RasterOutputJson.Serialize(descriptor)), Substitute.For<IProcessCatalog>());

        var artifact = package.Artifacts.Should().ContainSingle().Subject;
        artifact.Uri.Should().BeNull();
        artifact.ContentType.Should().BeNull();
        artifact.Metadata.Should().ContainKey(RasterOutputArtifactMetadata.Unsupported);
    }

    private static ExecutionJobRecord CreateSucceededJob(string reference)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "job-x",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            CompletedAt = now,
            AttemptCount = 1,
            ArtifactReferences = [reference],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "raster.resample"
            }
        };
    }

    private static AnalysisResultPackage CreateStagedPackage()
    {
        var descriptor = new StagedObjectRasterOutputDescriptor
        {
            JobId = "job-x",
            AttemptNumber = 1,
            OutputName = "output1",
            Content = new RasterContentIdentity
            {
                SizeBytes = 10,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('a', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = Honua.Core.Features.Infrastructure.Domain.CloudStorageProvider.Local,
            StoreReference = "gp-outputs",
            ObjectKey = "gp/outputs/job-x/a1/output1/result.tif",
        };

        return GeoprocessingResultPackageFactory.Create(
            CreateSucceededJob(RasterOutputJson.Serialize(descriptor)), Substitute.For<IProcessCatalog>());
    }
}
