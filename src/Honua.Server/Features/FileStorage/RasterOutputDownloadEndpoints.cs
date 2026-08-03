// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.IO;
using Honua.Geoprocessing;
using Honua.Infrastructure.Models;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Stable, authenticated download surface for immutable GP raster outputs. Provider
/// locators and credentials remain behind the registered object-store abstraction.
/// </summary>
internal static class RasterOutputDownloadEndpoints
{
    private const string RoutePrefix =
        "/api/v{version:apiVersion}/geoprocessing/raster-outputs";

    public static IEndpointRouteBuilder MapRasterOutputDownloadEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Handler-authorized: resolving the descriptor yields the owning job identity,
        // then IGeoprocessingJobService applies the canonical owner/grant check before
        // any object stream is opened. AllowAnonymous keeps 401/403 ordering inside that
        // shared authorization path for API-key and bearer-token callers alike.
        var group = endpoints.MapGroup(RoutePrefix)
            .WithApiVersionSet()
            .HasApiVersion(1, 0);

        group.MapMethods(
                "/{artifactId}",
                [HttpMethods.Get, HttpMethods.Head],
                static (string artifactId, HttpContext context, CancellationToken cancellationToken) =>
                    HandleDownloadAsync(
                        artifactId,
                        context,
                        context.RequestServices.GetService<IRasterOutputRegistry>(),
                        context.RequestServices.GetService<IRasterOutputObjectStore>(),
                        context.RequestServices.GetService<IGeoprocessingJobService>(),
                        cancellationToken))
            .WithName("GeoprocessingRasterOutputDownload")
            .WithDisplayName("Download GP Raster Output")
            .WithSummary("Stream an immutable geoprocessing COG output")
            .WithDescription(
                "Streams a visible COG through a stable Honua URL without exposing backing object-store credentials.")
            .WithTags("Geoprocessing", "Raster")
            .AllowAnonymous();

        return endpoints;
    }

    internal static async Task<IResult> HandleDownloadAsync(
        string artifactId,
        HttpContext context,
        IRasterOutputRegistry? registry,
        IRasterOutputObjectStore? objectStore,
        IGeoprocessingJobService? jobService,
        CancellationToken cancellationToken)
    {
        if (!RasterOutputIdentity.IsArtifactId(artifactId)
            || registry is null
            || objectStore is null
            || jobService is null)
        {
            return NotFound(context);
        }

        var resolution = await registry.ResolveVisibleAsync(artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (!IsDownloadableCog(resolution, artifactId))
        {
            return NotFound(context);
        }

        try
        {
            _ = await jobService.GetJobAsync(
                resolution!.Output.Lineage.JobId,
                context.User,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GeoprocessingAuthorizationException exception)
        {
            return exception.RequiresAuthentication
                ? StandardErrorHelpers.CreateUnauthorized(context, exception.Message)
                : StandardErrorHelpers.CreateForbidden(context, exception.Message);
        }
        catch (GeoprocessingNotFoundException)
        {
            // Do not expose a detached registry row when its owning job is no longer
            // caller-visible under the canonical result-retention contract.
            return NotFound(context);
        }

        IAsyncDisposable? lease = await registry.AcquireObjectLeaseAsync(
            resolution!.PublishedObject.StoreReference,
            resolution.PublishedObject.ObjectKey,
            cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-resolve under the same lease the sweeper uses. This closes the gap
            // between authorization and object open if a result expires concurrently.
            resolution = await registry.ResolveVisibleAsync(artifactId, cancellationToken)
                .ConfigureAwait(false);
            if (!IsDownloadableCog(resolution, artifactId))
            {
                return NotFound(context);
            }

            var descriptor = resolution!.PublishedObject;
            ApplyHeaders(context.Response, descriptor);
            if (HttpMethods.IsHead(context.Request.Method))
            {
                return TypedResults.Empty;
            }

            var stream = await objectStore.OpenReadAsync(
                descriptor.StoreReference,
                descriptor.ObjectKey,
                cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return NotFound(context);
            }

            var leasedStream = new LeaseOwnedReadStream(stream, lease);
            lease = null;
            return Results.Stream(
                leasedStream,
                descriptor.Content.MediaType,
                fileDownloadName: BuildDownloadFileName(descriptor.OutputName),
                enableRangeProcessing: false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsDownloadableCog(
        RasterOutputRegistrationResolution? resolution,
        string artifactId) =>
        resolution is not null
        && resolution.Output is ObjectStoreRasterOutputDescriptor
        && resolution.PublishedObject.Encoding == RasterOutputEncoding.CloudOptimizedGeoTiff
        && string.Equals(resolution.PublishedObject.ArtifactId, artifactId, StringComparison.Ordinal)
        && string.Equals(resolution.Output.ArtifactId, artifactId, StringComparison.Ordinal);

    private static void ApplyHeaders(
        HttpResponse response,
        ObjectStoreRasterOutputDescriptor descriptor)
    {
        var checksum = descriptor.Content.Checksum!;
        response.ContentType = descriptor.Content.MediaType;
        response.ContentLength = descriptor.Content.SizeBytes;
        response.Headers[HeaderNames.ETag] = $"\"{checksum.Algorithm}-{checksum.Value.ToLowerInvariant()}\"";
        response.Headers[HeaderNames.CacheControl] = "private, no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static string BuildDownloadFileName(string outputName) =>
        outputName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || outputName.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
            ? outputName
            : outputName + ".tif";

    private static IResult NotFound(HttpContext context) =>
        StandardErrorHelpers.CreateNotFound(context, "Raster output was not found or is no longer visible.");

    private sealed class LeaseOwnedReadStream(
        Stream inner,
        IAsyncDisposable lease) : DelegatingStream(inner)
    {
        private IAsyncDisposable? _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                Interlocked.Exchange(ref _lease, null)?
                    .DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                var ownedLease = Interlocked.Exchange(ref _lease, null);
                if (ownedLease is not null)
                {
                    await ownedLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
