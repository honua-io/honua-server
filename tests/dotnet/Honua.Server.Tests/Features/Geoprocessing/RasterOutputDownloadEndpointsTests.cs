// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.Server.Features.FileStorage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class RasterOutputDownloadEndpointsTests
{
    [Fact]
    public void ResultPackage_UsesStableDownloadRouteInsteadOfProviderLocator()
    {
        var output = CreateOutput([1, 2, 3]);
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var job = new ExecutionJobRecord
        {
            OperationId = output.Lineage.JobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            ArtifactReferences = [RasterOutputArtifactReference.CreateOutput(output)],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test"
            }
        };

        var package = GeoprocessingResultPackageFactory.Create(job, new BuiltInProcessCatalog());

        var artifact = Assert.Single(package.Artifacts);
        Assert.Equal(
            "/api/v1/geoprocessing/raster-outputs/" + output.ArtifactId,
            artifact.Uri);
        Assert.DoesNotContain(output.StoreReference, artifact.Uri!, StringComparison.Ordinal);
        Assert.DoesNotContain(output.ObjectKey, artifact.Uri!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_KeepsCleanupLeaseUntilStreamCompletes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var output = CreateOutput(bytes);
        var resolution = new RasterOutputRegistrationResolution(
            output,
            output,
            RasterOutputRegistrationKind.ResultArtifact);
        var registry = Substitute.For<IRasterOutputRegistry>();
        var objectStore = Substitute.For<IRasterOutputObjectStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var lease = new TrackingLease();
        registry.ResolveVisibleAsync(output.ArtifactId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterOutputRegistrationResolution?>(resolution));
        // NSubstitute consumes the first ValueTask only to identify the configured call;
        // the callback returns a fresh instance for every runtime invocation.
#pragma warning disable CA2012
        SubstituteExtensions.Returns(
            registry.AcquireObjectLeaseAsync(
                output.StoreReference,
                output.ObjectKey,
                Arg.Any<CancellationToken>()),
            _ => ValueTask.FromResult<IAsyncDisposable>(lease));
#pragma warning restore CA2012
        objectStore.OpenReadAsync(
                output.StoreReference,
                output.ObjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false)));
        jobService.GetJobAsync(
                output.Lineage.JobId,
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ExecutionJobRecord>(null!));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner-1")], "test"))
        };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = responseBody;

        var result = await RasterOutputDownloadEndpoints.HandleDownloadAsync(
            output.ArtifactId,
            context,
            registry,
            objectStore,
            jobService,
            CancellationToken.None);

        Assert.False(lease.IsDisposed);
        await result.ExecuteAsync(context);

        Assert.True(lease.IsDisposed);
        Assert.Equal(bytes, responseBody.ToArray());
        Assert.Equal("image/tiff", context.Response.ContentType);
        Assert.Equal(bytes.LongLength, context.Response.ContentLength);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
        await jobService.Received(1).GetJobAsync(
            output.Lineage.JobId,
            context.User,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Download_RefusesObjectOpenWhenOwningJobIsUnauthorized()
    {
        var output = CreateOutput([1, 2, 3]);
        var resolution = new RasterOutputRegistrationResolution(
            output,
            output,
            RasterOutputRegistrationKind.ResultArtifact);
        var registry = Substitute.For<IRasterOutputRegistry>();
        var objectStore = Substitute.For<IRasterOutputObjectStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        registry.ResolveVisibleAsync(output.ArtifactId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterOutputRegistrationResolution?>(resolution));
        jobService.GetJobAsync(
                output.Lineage.JobId,
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ExecutionJobRecord>(
                new GeoprocessingAuthorizationException(requiresAuthentication: false)));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "other-user")], "test"))
        };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        var result = await RasterOutputDownloadEndpoints.HandleDownloadAsync(
            output.ArtifactId,
            context,
            registry,
            objectStore,
            jobService,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        await objectStore.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default);
        await registry.DidNotReceiveWithAnyArgs().AcquireObjectLeaseAsync(default!, default!, default);
    }

    private static ObjectStoreRasterOutputDescriptor CreateOutput(byte[] bytes)
    {
        var checksumValue = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var checksum = new RasterChecksum("sha256", checksumValue);
        return new ObjectStoreRasterOutputDescriptor
        {
            ArtifactId = RasterOutputIdentity.CreateArtifactId("job-42", "result", checksum),
            OutputName = "result",
            StoreReference = "gp-results",
            ObjectKey = "raster/published/01/result.tif",
            ObjectVersion = "version-1",
            Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
            Content = new RasterContentIdentity
            {
                SizeBytes = bytes.LongLength,
                MediaType = "image/tiff",
                Checksum = checksum
            },
            Grid = new RasterGridMetadata
            {
                Crs = "EPSG:4326",
                Width = 1,
                Height = 1,
                BandCount = 1,
                GeoTransform = [0, 1, 0, 1, 0, -1]
            },
            Engine = new RasterProducingEngine("gdal", "3.11.0"),
            Lineage = new RasterOutputLineage
            {
                JobId = "job-42",
                Attempt = 0,
                ProcessId = "raster.reproject"
            },
            Retention = new RasterOutputRetention(
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero))
        };
    }

    private sealed class TrackingLease : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
