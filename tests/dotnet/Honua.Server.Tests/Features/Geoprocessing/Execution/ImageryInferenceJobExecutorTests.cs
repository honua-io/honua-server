// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Geoprocessing.Inference;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Delegated imagery/ML inference lane coverage (#2241). These tests drive the
/// production <see cref="ImageryInferenceJobExecutor"/> with the REAL
/// <see cref="HttpImageryInferenceClient"/> adapter over a stubbed HTTP handler
/// (no live cloud), pinning the acceptance contract: configured-backend happy
/// paths for raster and feature outputs (georeferencing preserved byte-for-byte),
/// the clear not-configured / recognized-but-unsupported-provider failures (no
/// silent stub, no fake result), sanitized backend-error surfaces (no endpoint,
/// no credentials), and secret-reference API key resolution.
/// </summary>
public sealed class ImageryInferenceJobExecutorTests
{
    // A real, minimally-valid GeoTIFF fixture scene: 64x64 pixels at 10m, origin
    // (500000, 4600000), EPSG:32610. Built byte-by-byte so the executor's IFD
    // parse and georeferencing checks run against genuine GeoTIFF structure.
    private static readonly byte[] SourceGeoTiff =
        BuildGeoTiff(width: 64, height: 64, originX: 500000, originY: 4600000, pixelSize: 10, epsg: 32610);

    // The "classification map" the fake backend answers with: same extent and CRS
    // as the source (as the contract requires) but resampled to 32x32 at 20m,
    // which is legitimate for a segmentation output. The executor must land these
    // bytes UNMODIFIED so the backend-emitted georeferencing survives intact.
    private static readonly byte[] ClassifiedGeoTiff =
        BuildGeoTiff(width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20, epsg: 32610);

    // Same shape, but relocated ~10km east and in a different CRS — the payload a
    // misbehaving backend would return that must never be published.
    private static readonly byte[] MislocatedGeoTiff =
        BuildGeoTiff(width: 32, height: 32, originX: 510000, originY: 4600000, pixelSize: 20, epsg: 32611);

    // Valid TIFF structure with NO GeoTIFF model/CRS tags — the "plain TIFF that
    // starts with II*\0" case that magic-byte checking alone would wave through.
    private static readonly byte[] UnreferencedTiff =
        BuildGeoTiff(width: 32, height: 32, originX: 0, originY: 0, pixelSize: 0, epsg: 0, georeferenced: false);

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "http");
        var context = CreateContext("op-wrong-id");

        var record = CreateJobRecord(processId: "geometry.buffer");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("imagery.classify");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_NoBackendConfigured_FailsWithClearUnavailableMessage()
    {
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "");
        var context = CreateContext("op-not-configured");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("no cloud inference backend is configured");
        result.ErrorMessage.Should().Contain("Geoprocessing:ImageryInference",
            "the unavailability message must tell the operator exactly what to configure");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_RecognizedUnsupportedProvider_FailsClearly()
    {
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "sagemaker");
        var context = CreateContext("op-sagemaker");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("sagemaker");
        result.ErrorMessage.Should().Contain("not yet supported");
        result.ErrorMessage.Should().Contain("http", "the failure must point at the supported adapter");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_UnknownProvider_FailsWithSupportedProviderList()
    {
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "mystery-ml");
        var context = CreateContext("op-unknown");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("mystery-ml");
        result.ErrorMessage.Should().Contain("http");
        result.ErrorMessage.Should().Contain("vertex");
    }

    [UnitTest]
    public async Task ExecuteAsync_RasterOutput_PublishesGeoTiffArtifactPreservingBackendBytes()
    {
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http", apiKey: "test-key");
        var context = CreateContext("op-raster", out var published);

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        published.Value.Should().StartWith(ImageryInferenceJobExecutor.GeoTiffDataUriPrefix);
        var artifactBytes = Convert.FromBase64String(
            published.Value![ImageryInferenceJobExecutor.GeoTiffDataUriPrefix.Length..]);
        artifactBytes.Should().Equal(ClassifiedGeoTiff,
            "the raster must be passed through byte-for-byte so the backend-preserved extent/CRS lands intact");

        // The outgoing delegation request carries the model reference, task, and
        // the base64 source scene, authorized with the configured key.
        handler.LastRequestBody.Should().NotBeNull();
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.GetProperty("model").GetString().Should().Be("landcover-v2");
        request.RootElement.GetProperty("task").GetString().Should().Be("classification");
        request.RootElement.GetProperty("image").GetBytesFromBase64().Should().Equal(SourceGeoTiff);
        handler.LastAuthorization.Should().Be("Bearer test-key");
    }

    [UnitTest]
    public async Task ExecuteAsync_FeaturesOutput_PublishesFeatureCollectionArtifact()
    {
        // Coordinates must sit inside the EPSG:32610 (UTM 10N) source scene's zone:
        // the executor verifies detection placement against the source footprint,
        // so an arbitrary lon/lat like [10, 20] would be (correctly) rejected as
        // being on another continent.
        const string featureCollection =
            """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{"class":"building","score":0.91}}]}""";
        var handler = new StubHttpHandler(_ => JsonResponse(
            $$"""{"outputType":"features","features":{{featureCollection}}}"""));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-features", out var published);

        var record = CreateJobRecord(task: "detection", confidenceThreshold: "0.5");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        published.Value.Should().StartWith(FeatureCollectionArtifact.DataUriPrefix);
        var payload = Convert.FromBase64String(
            published.Value![FeatureCollectionArtifact.DataUriPrefix.Length..]);
        using var artifact = JsonDocument.Parse(payload);
        artifact.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        artifact.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);

        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.GetProperty("task").GetString().Should().Be("detection");
        request.RootElement.GetProperty("confidenceThreshold").GetDouble().Should().BeApproximately(0.5, 1e-9);
    }

    [UnitTest]
    public async Task ExecuteAsync_BackendHttpError_FailsWithSanitizedMessage()
    {
        var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("model exploded at /var/models/secret-path", Encoding.UTF8, "text/plain")
            });
        var executor = CreateExecutor(handler, provider: "http", apiKey: "super-secret-key");
        var context = CreateContext("op-500");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("HTTP 500");
        result.ErrorMessage.Should().NotContain("inference.example.com",
            "the endpoint must never leak into the job status");
        result.ErrorMessage.Should().NotContain("super-secret-key",
            "credentials must never leak into the job status");
        result.ErrorMessage.Should().NotContain("secret-path",
            "raw provider response bodies must never leak into the job status");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_SecretReferenceApiKey_ResolvesThroughSecretProvider()
    {
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var secretProvider = Substitute.For<ISecretProvider>();
        secretProvider.IsSecretReference("secret://inference/api-key").Returns(true);
        secretProvider
            .GetSecretOrDefaultAsync("secret://inference/api-key", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("resolved-from-store");
        var executor = CreateExecutor(
            handler, provider: "http", apiKey: "secret://inference/api-key", secretProvider: secretProvider);
        var context = CreateContext("op-secret", out _);

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        handler.LastAuthorization.Should().Be("Bearer resolved-from-store");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingModel_FailsCleanly()
    {
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "http");
        var context = CreateContext("op-no-model");

        var record = CreateJobRecord(omitModel: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("model");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingModel_UsesConfiguredDefaultModel()
    {
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http", defaultModel: "fallback-model");
        var context = CreateContext("op-default-model", out _);

        var record = CreateJobRecord(omitModel: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.GetProperty("model").GetString().Should().Be("fallback-model");
    }

    [UnitTest]
    public async Task ExecuteAsync_NonTiffSource_FailsBeforeDelegation()
    {
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-bad-source");

        var record = CreateJobRecord(source: Convert.ToBase64String("not a tiff"u8.ToArray()));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("source");
        handler.LastRequestBody.Should().BeNull("invalid input must be rejected before any cloud call");
    }

    [UnitTest]
    public async Task ExecuteAsync_NonTiffBackendRaster_FailsWithHonestyMessage()
    {
        // A backend answering PNG bytes (no georeferencing) must be rejected, not
        // landed as a fake "GeoTIFF" artifact.
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var executor = CreateExecutor(new StubHttpHandler(_ => RasterResponse(pngBytes)), provider: "http");
        var context = CreateContext("op-png");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("TIFF");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_PlaintextHttpEndpoint_RefusesBeforeSendingSecrets()
    {
        // The API key AND the full source scene leave the process on this request,
        // so a plaintext http:// endpoint to a remote host must be refused rather
        // than silently transmitting credentials and imagery in the clear.
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(
            handler, provider: "http", apiKey: "super-secret-key",
            endpoint: "http://inference.example.com/v1/infer");
        var context = CreateContext("op-plaintext");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("plaintext http");
        result.ErrorMessage.Should().NotContain("super-secret-key");
        handler.LastRequestBody.Should().BeNull("nothing may be sent over an unencrypted connection");
    }

    [UnitTest]
    public async Task ExecuteAsync_PlaintextLoopbackEndpoint_IsAllowedForLocalDevelopment()
    {
        // Loopback traffic never leaves the machine, and a local model server is a
        // legitimate development workflow, so http://127.0.0.1 stays permitted.
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(
            handler, provider: "http", endpoint: "http://127.0.0.1:8000/infer");
        var context = CreateContext("op-loopback", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_UnreferencedTiffOutput_IsRejectedNotPublished()
    {
        // A plain TIFF also starts with II*\0. Magic-byte checking alone would
        // publish it as a "GeoTIFF" at an unknown location; the IFD parse must
        // catch the missing model/CRS tags.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(UnreferencedTiff)), provider: "http");
        var context = CreateContext("op-unreferenced");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("georeferencing");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MislocatedOutput_IsRejectedAgainstTheSourceExtent()
    {
        // Output claims a different CRS and a 10km-shifted origin: publishing it
        // would place the classification at the wrong location while the process
        // advertises georeferencing preservation.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(MislocatedGeoTiff)), provider: "http");
        var context = CreateContext("op-mislocated");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("does not match the source");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MalformedFeatureCollection_IsRejectedByTheSharedReader()
    {
        // A non-array features member passes a naive discriminator check but is an
        // unusable artifact; the shared GeoJSON codec must reject it.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","features":"not-an-array"}}""")),
            provider: "http");
        var context = CreateContext("op-malformed-fc");

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("FeatureCollection");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_UnreferencedSource_IsRejectedBeforeDelegation()
    {
        // An unreferenced source makes the output-location contract unverifiable.
        // Treating that as permission to skip the comparison would let the backend
        // return any georeferenced raster, so it is refused up front.
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-unreferenced-source");

        var record = CreateJobRecord(source: Convert.ToBase64String(UnreferencedTiff));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("georeferencing");
        handler.LastRequestBody.Should().BeNull("an unverifiable source must never be delegated");
    }

    [UnitTest]
    public async Task ExecuteAsync_SendsSourceCrsToTheBackend()
    {
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-source-crs", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.GetProperty("sourceCrs").GetInt32().Should().Be(32610,
            "the backend needs the scene CRS to georeference its result");
    }

    [UnitTest]
    public async Task ExecuteAsync_ProjectedFeatureCoordinates_AreRejectedNotPublished()
    {
        // The source is EPSG:32610, so a backend echoing detections in source-CRS
        // metres yields coordinates far outside lon/lat range. Downstream consumers
        // read this artifact through a 4326-fixed factory, so publishing it would
        // silently relocate every detection.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[500123.0,4600456.0]},"properties":{"class":"building"}}]}}""")),
            provider: "http");
        var context = CreateContext("op-projected-features");

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("WGS 84");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_TiepointWithRasterOffset_IsTreatedAsTheSameGrid()
    {
        // Same grid expressed with a non-zero raster tiepoint (i=8, j=8) and a
        // correspondingly shifted model point. Discarding (i, j) would compute a
        // different origin and reject this valid output as mislocated.
        var offsetEquivalent = BuildGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20, epsg: 32610,
            tiepointI: 8, tiepointJ: 8);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(offsetEquivalent)), provider: "http");
        var context = CreateContext("op-tiepoint-offset", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded,
            "a non-zero ModelTiepoint raster offset describes the same grid");
    }

    [UnitTest]
    public async Task ExecuteAsync_RotatedOutputTransform_IsRejectedNotPublished()
    {
        // A sheared/rotated ModelTransformation can share the source corner, CRS,
        // dimensions, and diagonal magnitudes while covering different ground.
        // Comparing only |m00| / |m11| would let it through.
        var rotated = BuildMatrixGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000,
            scaleX: 20, scaleY: -20, shearX: 5, shearY: 5, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(rotated)), provider: "http");
        var context = CreateContext("op-rotated");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("rotated or sheared");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_AxisFlippedOutputTransform_IsRejectedNotPublished()
    {
        // Positive m11 means the Y axis increases southward — a vertical flip that
        // Math.Abs on the diagonal would have hidden entirely.
        var flipped = BuildMatrixGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000,
            scaleX: 20, scaleY: 20, shearX: 0, shearY: 0, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(flipped)), provider: "http");
        var context = CreateContext("op-flipped");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("north-up");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_NorthUpMatrixTransform_IsAccepted()
    {
        // The same grid expressed via ModelTransformation instead of
        // ModelPixelScale + ModelTiepoint must still be accepted.
        var matrixForm = BuildMatrixGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000,
            scaleX: 20, scaleY: -20, shearX: 0, shearY: 0, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(matrixForm)), provider: "http");
        var context = CreateContext("op-matrix", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_HeaderOnlyTiffOutput_IsRejectedNotPublished()
    {
        // Size, transform, and CRS tags but no strip offsets / byte counts: a
        // structurally parseable header with no pixels is not a usable
        // classification raster.
        var headerOnly = BuildGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20,
            epsg: 32610, withRasterData: false);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(headerOnly)), provider: "http");
        var context = CreateContext("op-header-only");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("header-only");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_SubPixelOriginShift_IsRejected()
    {
        // A shift well under one SOURCE pixel (10m) but far above float noise
        // still relocates the classification on the ground, so it must fail.
        var shifted = BuildGeoTiff(
            width: 32, height: 32, originX: 500004, originY: 4600000, pixelSize: 20, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(shifted)), provider: "http");
        var context = CreateContext("op-subpixel-shift");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("origin");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_RedirectResponse_IsRefusedNotFollowed()
    {
        // A 307/308 preserves the POST body, so following an https endpoint's
        // redirect to an unvalidated destination would resend the key and the
        // whole raster. The handler is configured not to follow redirects; the
        // adapter refuses one outright if it ever surfaces.
        var handler = new StubHttpHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("http://evil.example.com/collect");
            return redirect;
        });
        var executor = CreateExecutor(handler, provider: "http", apiKey: "super-secret-key");
        var context = CreateContext("op-redirect");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("redirect");
        result.ErrorMessage.Should().NotContain("evil.example.com");
        result.ErrorMessage.Should().NotContain("super-secret-key");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_TruncatedRasterStorage_IsRejectedNotPublished()
    {
        // Strip tags survive but the pixels they point at do not: the declared
        // storage range runs past the end of the payload.
        var truncated = BuildGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20, epsg: 32610);
        Array.Resize(ref truncated, truncated.Length - 512);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(truncated)), provider: "http");
        var context = CreateContext("op-truncated");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("header-only");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_ExcessivelyCoarseOutput_IsRejectedNotWavedThrough()
    {
        // Codex's worked example: a 640-unit source and a 1x1 result with a
        // 1000-unit pixel differ by 360 units, which a whole-output-pixel
        // tolerance would have accepted as "extent preserved".
        var coarse = BuildGeoTiff(
            width: 1, height: 1, originX: 500000, originY: 4600000, pixelSize: 1000, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(coarse)), provider: "http");
        var context = CreateContext("op-coarse");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("too coarse");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_PixelIsPointOutputWithSameTiepoint_IsRejectedAsHalfPixelShift()
    {
        // Source is PixelIsArea (tiepoint = corner). An output declaring
        // PixelIsPoint (tiepoint = pixel CENTRE) with the SAME tiepoint really
        // covers ground shifted by half its pixel, which collapsing the
        // GeoKeyDirectory to a bare CRS code would have accepted.
        var pixelIsPoint = BuildGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20,
            epsg: 32610, rasterType: 2);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(pixelIsPoint)), provider: "http");
        var context = CreateContext("op-pixel-is-point");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("origin");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_PixelIsPointOutputDescribingTheSameGrid_IsAccepted()
    {
        // The same coverage honestly expressed under the point convention: the
        // tiepoint sits half a pixel in from the corner. Normalizing both to the
        // corner convention must make these compare equal.
        var equivalent = BuildGeoTiff(
            width: 32, height: 32, originX: 500010, originY: 4599990, pixelSize: 20,
            epsg: 32610, rasterType: 2);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(equivalent)), provider: "http");
        var context = CreateContext("op-pixel-is-point-equivalent", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded,
            "a PixelIsPoint tiepoint half a pixel in from the corner describes the same grid");
    }

    [UnitTest]
    public async Task ExecuteAsync_MultiStripTruncatedAfterFirstSegment_IsRejected()
    {
        // Two declared strips where only the first survives the truncation:
        // validating just the leading segment would publish a corrupt raster.
        var multiStrip = BuildMultiStripGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20,
            epsg: 32610, truncateAfterFirstStrip: true);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(multiStrip)), provider: "http");
        var context = CreateContext("op-multistrip-truncated");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("header-only");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MultiStripComplete_IsAccepted()
    {
        var multiStrip = BuildMultiStripGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000, pixelSize: 20,
            epsg: 32610, truncateAfterFirstStrip: false);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(multiStrip)), provider: "http");
        var context = CreateContext("op-multistrip-ok", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_InRangeButOffFootprintDetections_AreRejected()
    {
        // Codex's example: [10, 20] is numerically valid lon/lat, so a global
        // bounds test accepts it — but the source is an EPSG:32610 (UTM 10N)
        // scene, whose zone spans roughly -129..-117 longitude. Publishing this
        // would place the detection on another continent.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[10.0,20.0]},"properties":{"class":"building"}}]}}""")),
            provider: "http");
        var context = CreateContext("op-off-footprint");

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("source footprint");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_DetectionsInsideTheUtmZone_AreAccepted()
    {
        // Same UTM 10N scene; a detection at -122.4, 37.8 (San Francisco) sits in
        // the zone and must still be published.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{"class":"building"}}]}}""")),
            provider: "http");
        var context = CreateContext("op-on-footprint", out _);

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_UserDefinedSourceCrs_IsRejectedWithSpecificMessage()
    {
        // GeoKey 32767 means the CRS is spelled out in further tags rather than
        // named by EPSG. The lane cannot resolve that, and says so precisely
        // instead of claiming the file has no georeferencing at all.
        var userDefined = BuildGeoTiff(
            width: 64, height: 64, originX: 500000, originY: 4600000, pixelSize: 10, epsg: 32767);
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-user-defined-crs");

        var record = CreateJobRecord(source: Convert.ToBase64String(userDefined));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("user-defined");
        handler.LastRequestBody.Should().BeNull("an unverifiable source must not be delegated");
    }

    [UnitTest]
    public async Task ExecuteAsync_OversizedFeaturePayload_IsRejectedBeforeParsing()
    {
        // The guard must fire on the raw payload, before UTF-16 decoding and NTS
        // object expansion multiply the footprint.
        var padding = new string('x', 4096);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                "{\"outputType\":\"features\",\"features\":{\"type\":\"FeatureCollection\",\"note\":\""
                + padding
                + "\",\"features\":[]}}")),
            provider: "http", maxArtifactBytes: 1024);
        var context = CreateContext("op-huge-features");

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_BigTiffWithOverflowingIfdOffset_FailsCleanlyNotByThrowing()
    {
        // A BigTIFF first-IFD offset near ulong.MaxValue makes a naive
        // "offset + needed > length" bounds check WRAP and pass, after which
        // narrowing to int yields a negative span index and throws out of the
        // parser — surfacing as an unexpected execution failure to be retried
        // rather than a curated invalid-input result.
        var malformed = new byte[64];
        malformed[0] = 0x49;
        malformed[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(2), 43);   // BigTIFF
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(4), 8);    // offset size
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(8), ulong.MaxValue - 4);

        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-bigtiff-overflow");

        var record = CreateJobRecord(source: Convert.ToBase64String(malformed));

        var act = async () => await executor.ExecuteAsync(record, context, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Subject.ErrorMessage.Should().Contain("source");
        handler.LastRequestBody.Should().BeNull();
    }

    [UnitTest]
    public async Task ExecuteAsync_DatelineCrossingSource_AcceptsNormalizedDetections()
    {
        // Scene at longitude 179 spanning 2 degrees: its footprint runs past 181,
        // but RFC 7946 requires the backend to report that same ground as about
        // -179. A naive numeric range test would reject this valid detection.
        var datelineSource = BuildGeoTiff(
            width: 32, height: 32, originX: 179, originY: 10, pixelSize: 0.0625, epsg: 4326);
        var datelineOutput = BuildGeoTiff(
            width: 32, height: 32, originX: 179, originY: 10, pixelSize: 0.0625, epsg: 4326);

        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-179.6,9.5]},"properties":{"class":"vessel"}}]}}""")),
            provider: "http");
        var context = CreateContext("op-dateline", out _);

        var record = CreateJobRecord(
            source: Convert.ToBase64String(datelineSource), task: "detection");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        _ = datelineOutput;
        result.Status.Should().Be(ExecutionJobStatus.Succeeded,
            "a detection normalized across the antimeridian still lies inside the source scene");
    }

    [UnitTest]
    public async Task ExecuteAsync_UnrecognizedOutputType_DoesNotEchoBackendContentIntoJobStatus()
    {
        // outputType is backend-controlled. Interpolating it into the failure
        // message would put provider-chosen content of unbounded length onto the
        // client-visible job status, contradicting the adapter's sanitization
        // contract.
        var hostile = "SECRET-" + new string('z', 5000);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                "{\"outputType\":\"" + hostile + "\"}")),
            provider: "http");
        var context = CreateContext("op-hostile-outputtype");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("unrecognized outputType");
        result.ErrorMessage.Should().NotContain("SECRET",
            "backend-controlled values must never be echoed onto the job status");
        result.ErrorMessage!.Length.Should().BeLessThan(500,
            "the job status must stay bounded regardless of what the backend sends");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_NonFiniteSourcePixelScale_IsRejectedBeforeDelegation()
    {
        // PositiveInfinity satisfies a bare "> 0" positivity test, and an infinite
        // extent then makes the mismatch comparison evaluate Infinity - Infinity
        // = NaN, which compares false against every tolerance and so reports a
        // MATCH. A crafted source must therefore be refused outright.
        var infiniteScale = BuildGeoTiff(
            width: 64, height: 64, originX: 500000, originY: 4600000,
            pixelSize: double.PositiveInfinity, epsg: 32610);
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-infinite-scale");

        var record = CreateJobRecord(source: Convert.ToBase64String(infiniteScale));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("non-finite");
        handler.LastRequestBody.Should().BeNull("a crafted source must not be delegated");
    }

    [UnitTest]
    public async Task ExecuteAsync_NonFiniteOutputPixelScale_IsRejectedNotMatched()
    {
        // Same hazard on the output side: an infinite extent must not be able to
        // slip through the comparison as "georeferencing preserved".
        var infiniteOutput = BuildGeoTiff(
            width: 32, height: 32, originX: 500000, originY: 4600000,
            pixelSize: double.PositiveInfinity, epsg: 32610);
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(infiniteOutput)), provider: "http");
        var context = CreateContext("op-infinite-output");

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_FeaturePayloadDeclaringNonWgs84Crs_IsRejected()
    {
        // The shared reader ignores the legacy crs member and builds geometries
        // through a factory fixed to SRID 4326, so an explicit EPSG:3857
        // declaration would have its coordinates silently reinterpreted as
        // degrees. The backend is telling us the payload is not what the contract
        // requires, so refuse it rather than ignore it.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::3857"}},"features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{}}]}}""")),
            provider: "http");
        var context = CreateContext("op-declared-crs");

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("non-WGS 84 CRS");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_FeaturePayloadDeclaringWgs84Crs_IsStillAccepted()
    {
        // A legacy but WGS 84 crs member is harmless and must not break a
        // conforming backend.
        var executor = CreateExecutor(
            new StubHttpHandler(_ => JsonResponse(
                """{"outputType":"features","features":{"type":"FeatureCollection","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:OGC:1.3:CRS84"}},"features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-122.4,37.8]},"properties":{}}]}}""")),
            provider: "http");
        var context = CreateContext("op-declared-crs-ok", out _);

        var result = await executor.ExecuteAsync(
            CreateJobRecord(task: "detection"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_ModelTagWithWrongFieldType_IsNotTreatedAsGeoreferenced()
    {
        // ModelPixelScale declared as SHORT rather than DOUBLE: reading 16 bytes
        // off it would run past the tag's declared payload and manufacture
        // georeferencing out of neighbouring IFD bytes.
        var spoofed = BuildGeoTiff(
            width: 64, height: 64, originX: 500000, originY: 4600000, pixelSize: 10,
            epsg: 32610, pixelScaleFieldType: 3);
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http");
        var context = CreateContext("op-wrong-field-type");

        var record = CreateJobRecord(source: Convert.ToBase64String(spoofed));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        handler.LastRequestBody.Should().BeNull(
            "a tag whose field type violates the spec must not yield usable georeferencing");
    }

    [UnitTest]
    public async Task ExecuteAsync_OversizedInlineSource_IsRejectedBeforeDecoding()
    {
        // The ceiling has to bite on the ENCODED value: decoding first would have
        // already allocated the array, and the outbound JSON request duplicates it
        // again, so a caller could drive allocation past MaxArtifactBytes before
        // any backend call.
        var oversized = Convert.ToBase64String(new byte[64 * 1024]);
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http", maxArtifactBytes: 4096);
        var context = CreateContext("op-oversized-source");

        var record = CreateJobRecord(source: oversized);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        result.ErrorMessage.Should().Contain("source");
        handler.LastRequestBody.Should().BeNull("an oversized source must never reach the backend");
    }

    [UnitTest]
    public async Task ExecuteAsync_SourceWithinCeiling_IsStillAccepted()
    {
        // Guard against the new bound over-rejecting a normal scene.
        var handler = new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff));
        var executor = CreateExecutor(handler, provider: "http", maxArtifactBytes: 64 * 1024);
        var context = CreateContext("op-source-within-ceiling", out _);

        var result = await executor.ExecuteAsync(CreateJobRecord(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task ExecuteAsync_OversizedRasterOutput_FailsWithGuardrail()
    {
        var executor = CreateExecutor(
            new StubHttpHandler(_ => RasterResponse(ClassifiedGeoTiff)), provider: "http", maxArtifactBytes: 8);
        var context = CreateContext("op-too-big");

        var record = CreateJobRecord();

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public void ResultPackage_FeatureOutput_IsPublishedUnderTheFeatureLayerSlot()
    {
        // imagery.classify declares [Raster, FeatureLayer] because the backend
        // decides which shape a scene yields. Positional slot assignment would
        // label a single published GeoJSON artifact as Raster/outputRaster and
        // leave the advertised outputFeatureLayer unreachable.
        var job = CreateTerminalJobRecord(
            FeatureCollectionArtifact.DataUriPrefix + Convert.ToBase64String("{}"u8.ToArray()));

        var package = GeoprocessingResultPackageFactory.Create(job, new BuiltInProcessCatalog());

        package.Artifacts.Should().ContainSingle();
        package.Artifacts[0].Kind.Should().Be(ArtifactKind.FeatureLayer,
            "a GeoJSON result must land in the declared feature slot, not the raster slot");
        package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        package.Artifacts[0].ContentType.Should().Be("application/geo+json");
    }

    [UnitTest]
    public void ResultPackage_RasterOutput_StillUsesTheRasterSlot()
    {
        var job = CreateTerminalJobRecord(
            ImageryInferenceJobExecutor.GeoTiffDataUriPrefix + Convert.ToBase64String(ClassifiedGeoTiff));

        var package = GeoprocessingResultPackageFactory.Create(job, new BuiltInProcessCatalog());

        package.Artifacts.Should().ContainSingle();
        package.Artifacts[0].Kind.Should().Be(ArtifactKind.Raster);
        package.Artifacts[0].Label.Should().Be("outputRaster");
    }

    [UnitTest]
    public void OutputSlotResolver_RemapsFeatureArtifactForAlternativeOutputs()
    {
        // imagery.classify declares [Raster, FeatureLayer] as alternatives; a lone
        // GeoJSON artifact must land in the feature slot, not slot 0.
        ArtifactKind[] declared = [ArtifactKind.Raster, ArtifactKind.FeatureLayer];

        var remapped = OutputSlotResolver.TryResolveAlternativeSlot(
            FeatureCollectionArtifact.DataUriPrefix + "e30=", 0, declared, out var slot, out var kind);

        remapped.Should().BeTrue();
        slot.Should().Be(1);
        kind.Should().Be(ArtifactKind.FeatureLayer);
    }

    [UnitTest]
    public void OutputSlotResolver_LeavesSimultaneousOutputProcessesAlone()
    {
        // The [FeatureLayer, Table] analytics ops publish their outputs TOGETHER.
        // Their first artifact matches slot 0, so it must not be remapped, and a
        // later artifact is never remapped regardless of media type.
        ArtifactKind[] declared = [ArtifactKind.FeatureLayer, ArtifactKind.Table];
        var geoJson = FeatureCollectionArtifact.DataUriPrefix + "e30=";

        OutputSlotResolver.TryResolveAlternativeSlot(geoJson, 0, declared, out _, out _)
            .Should().BeFalse("the first artifact already matches its declared slot");
        OutputSlotResolver.TryResolveAlternativeSlot(geoJson, 1, declared, out _, out _)
            .Should().BeFalse("only the first published artifact may ever be remapped");
    }

    [UnitTest]
    public void Catalog_ImageryClassify_DeclaresOutputsAsAlternatives()
    {
        var definition = new BuiltInProcessCatalog().GetProcess(ImageryInferenceJobExecutor.HandledProcessId);

        definition.Should().NotBeNull();
        definition!.OutputsAreAlternatives.Should().BeTrue(
            "the backend emits exactly one of the declared shapes per run");
    }

    [UnitTest]
    public void Catalog_SimultaneousOutputProcesses_DoNotDeclareAlternatives()
    {
        // Guard the default: the flag must stay off for the analytics ops whose
        // outputs are produced together, so GPServer keeps advertising them as
        // required results.
        var catalog = new BuiltInProcessCatalog();

        foreach (var processId in new[]
                 {
                     "analytics.cluster", "analytics.spatial-join", "analytics.density",
                     "generalization.dissolve"
                 })
        {
            catalog.GetProcess(processId)!.OutputsAreAlternatives.Should().BeFalse(
                $"'{processId}' produces its declared outputs together");
        }
    }

    private static ExecutionJobRecord CreateTerminalJobRecord(string artifactReference)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] =
                ImageryInferenceJobExecutor.HandledProcessId,
            ["protocolProcessId"] = ImageryInferenceJobExecutor.HandledProcessId,
            // Names the OGC adapter stamps at submit time, one per DECLARED output.
            ["process.output.0"] = "outputRaster",
            ["process.output.1"] = "outputFeatureLayer"
        };

        return new ExecutionJobRecord
        {
            OperationId = "op-slot",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [artifactReference],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ImageryInferenceJobExecutor CreateExecutor(
        StubHttpHandler handler,
        string provider,
        string apiKey = "",
        string defaultModel = "",
        ISecretProvider? secretProvider = null,
        long maxArtifactBytes = 50L * 1024L * 1024L,
        string endpoint = "https://inference.example.com/v1/infer")
    {
        var inferenceOptions = new ImageryInferenceOptions
        {
            Provider = provider,
            Endpoint = endpoint,
            ApiKey = apiKey,
            DefaultModel = defaultModel,
            TimeoutSeconds = 30
        };
        var inferenceMonitor = Substitute.For<IOptionsMonitor<ImageryInferenceOptions>>();
        inferenceMonitor.CurrentValue.Returns(inferenceOptions);

        var executorOptions = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var executorMonitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        executorMonitor.CurrentValue.Returns(executorOptions);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var client = new HttpImageryInferenceClient(
            httpClientFactory,
            NullLogger<HttpImageryInferenceClient>.Instance,
            secretProvider);

        return new ImageryInferenceJobExecutor(
            inferenceMonitor,
            executorMonitor,
            [client],
            NullLogger<ImageryInferenceJobExecutor>.Instance);
    }

    private static IJobExecutionContext CreateContext(string operationId)
        => CreateContext(operationId, out _);

    private static IJobExecutionContext CreateContext(string operationId, out CapturedArtifact published)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns(operationId);

        var box = new CapturedArtifact(null);
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => box.Value = call.ArgAt<string>(0));
        published = box;
        return context;
    }

    private static ExecutionJobRecord CreateJobRecord(
        string processId = ImageryInferenceJobExecutor.HandledProcessId,
        string? source = null,
        bool omitModel = false,
        string? task = null,
        string? confidenceThreshold = null)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
            [prefix + "source"] = source ?? Convert.ToBase64String(SourceGeoTiff)
        };

        if (!omitModel)
        {
            parameters[prefix + "model"] = "landcover-v2";
        }

        if (task is not null)
        {
            parameters[prefix + "task"] = task;
        }

        if (confidenceThreshold is not null)
        {
            parameters[prefix + "confidenceThreshold"] = confidenceThreshold;
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }

    private static HttpResponseMessage RasterResponse(byte[] rasterBytes)
        => JsonResponse(
            $$"""{"outputType":"raster","raster":"{{Convert.ToBase64String(rasterBytes)}}"}""");

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    /// <summary>
    /// Builds a minimal but structurally valid classic little-endian TIFF. When
    /// <paramref name="georeferenced"/> is true it also carries the GeoTIFF model
    /// tags (ModelPixelScale 33550, ModelTiepoint 33922) and a GeoKeyDirectory
    /// (34735) declaring the projected CRS, so the executor's real IFD parse has
    /// genuine structure to validate rather than a magic-byte stub.
    /// </summary>
    private static byte[] BuildGeoTiff(
        int width,
        int height,
        double originX,
        double originY,
        double pixelSize,
        int epsg,
        bool georeferenced = true,
        double tiepointI = 0,
        double tiepointJ = 0,
        bool withRasterData = true,
        int rasterType = 1,
        ushort pixelScaleFieldType = 12)
    {
        // Entries (ascending tag order, as TIFF requires): ImageWidth, ImageLength,
        // StripOffsets, StripByteCounts [, ModelPixelScale, ModelTiepoint,
        // GeoKeyDirectory]. The strip tags plus a real pixel block matter: a
        // header-only TIFF is refused by the executor, so the happy-path fixtures
        // must carry actual raster data to prove a readable artifact.
        var entryCount = (georeferenced ? 5 : 2) + (withRasterData ? 2 : 0);
        var ifdOffset = 8;
        var ifdSize = 2 + (entryCount * 12) + 4;
        var dataStart = ifdOffset + ifdSize;

        var pixelScaleOffset = dataStart;
        var tiepointOffset = pixelScaleOffset + 24;   // 3 doubles
        var geoKeyOffset = tiepointOffset + 48;       // 6 doubles
        var pixelDataOffset = georeferenced ? geoKeyOffset + 24 : dataStart;
        var pixelDataLength = withRasterData ? width * height : 0;
        var totalLength = pixelDataOffset + pixelDataLength;

        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();

        // Header: "II", 42, first-IFD offset.
        span[0] = 0x49;
        span[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)ifdOffset);

        BinaryPrimitives.WriteUInt16LittleEndian(span[ifdOffset..], (ushort)entryCount);
        var entry = ifdOffset + 2;

        WriteLongEntry(span, ref entry, tag: 256, value: (uint)width);
        WriteLongEntry(span, ref entry, tag: 257, value: (uint)height);
        if (withRasterData)
        {
            WriteLongEntry(span, ref entry, tag: 273, value: (uint)pixelDataOffset);
            WriteLongEntry(span, ref entry, tag: 279, value: (uint)pixelDataLength);

            // Deterministic, non-empty pixel block so the payload is a readable
            // raster rather than a bare header.
            for (var p = 0; p < pixelDataLength; p++)
            {
                span[pixelDataOffset + p] = (byte)(p % 251);
            }
        }

        if (georeferenced)
        {
            WriteOffsetEntry(
                span, ref entry, tag: 33550, type: pixelScaleFieldType, count: 3, offset: (uint)pixelScaleOffset);
            WriteOffsetEntry(span, ref entry, tag: 33922, type: 12, count: 6, offset: (uint)tiepointOffset);
            WriteOffsetEntry(span, ref entry, tag: 34735, type: 3, count: 12, offset: (uint)geoKeyOffset);

            // ModelPixelScale (sx, sy, sz)
            BinaryPrimitives.WriteDoubleLittleEndian(span[pixelScaleOffset..], pixelSize);
            BinaryPrimitives.WriteDoubleLittleEndian(span[(pixelScaleOffset + 8)..], pixelSize);
            BinaryPrimitives.WriteDoubleLittleEndian(span[(pixelScaleOffset + 16)..], 0d);

            // ModelTiepoint (i, j, k, x, y, z): raster point (i, j) -> its model
            // point. With the default (0, 0) that model point IS the upper-left
            // corner; with an offset the corner is walked back along the scale.
            BinaryPrimitives.WriteDoubleLittleEndian(span[tiepointOffset..], tiepointI);
            BinaryPrimitives.WriteDoubleLittleEndian(span[(tiepointOffset + 8)..], tiepointJ);
            BinaryPrimitives.WriteDoubleLittleEndian(
                span[(tiepointOffset + 24)..], originX + (tiepointI * pixelSize));
            BinaryPrimitives.WriteDoubleLittleEndian(
                span[(tiepointOffset + 32)..], originY - (tiepointJ * pixelSize));

            // GeoKeyDirectory: version/revision/minor/numberOfKeys, then one key
            // (ProjectedCSTypeGeoKey = 3072) stored in-line.
            // header: version, revision, minor, numberOfKeys — then keys in
            // ascending id order: 1025 GTRasterType, 3072 ProjectedCSType.
            BinaryPrimitives.WriteUInt16LittleEndian(span[geoKeyOffset..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 2)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 4)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 6)..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 8)..], 1025);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 10)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 12)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 14)..], (ushort)rasterType);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 16)..], 3072);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 18)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 20)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 22)..], (ushort)epsg);
        }

        return buffer;
    }

    /// <summary>
    /// Builds a GeoTIFF that georeferences via ModelTransformation (34264) rather
    /// than ModelPixelScale + ModelTiepoint, so axis signs and the off-diagonal
    /// rotation/shear terms can be exercised directly.
    /// </summary>
    private static byte[] BuildMatrixGeoTiff(
        int width,
        int height,
        double originX,
        double originY,
        double scaleX,
        double scaleY,
        double shearX,
        double shearY,
        int epsg)
    {
        // width, height, stripOffsets, stripByteCounts, transformation, geokeys
        const int entryCount = 6;
        var ifdOffset = 8;
        var dataStart = ifdOffset + 2 + (entryCount * 12) + 4;
        var matrixOffset = dataStart;
        var geoKeyOffset = matrixOffset + 128; // 16 doubles
        var pixelDataOffset = geoKeyOffset + 24;
        var pixelDataLength = width * height;
        var buffer = new byte[pixelDataOffset + pixelDataLength];
        var span = buffer.AsSpan();

        span[0] = 0x49;
        span[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[ifdOffset..], entryCount);

        var entry = ifdOffset + 2;
        WriteLongEntry(span, ref entry, tag: 256, value: (uint)width);
        WriteLongEntry(span, ref entry, tag: 257, value: (uint)height);
        WriteLongEntry(span, ref entry, tag: 273, value: (uint)pixelDataOffset);
        WriteLongEntry(span, ref entry, tag: 279, value: (uint)pixelDataLength);
        WriteOffsetEntry(span, ref entry, tag: 34264, type: 12, count: 16, offset: (uint)matrixOffset);
        WriteOffsetEntry(span, ref entry, tag: 34735, type: 3, count: 12, offset: (uint)geoKeyOffset);

        // Row-major 4x4: m00 m01 m02 m03 / m10 m11 m12 m13 / ...
        BinaryPrimitives.WriteDoubleLittleEndian(span[matrixOffset..], scaleX);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(matrixOffset + 8)..], shearX);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(matrixOffset + 24)..], originX);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(matrixOffset + 32)..], shearY);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(matrixOffset + 40)..], scaleY);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(matrixOffset + 56)..], originY);

        BinaryPrimitives.WriteUInt16LittleEndian(span[geoKeyOffset..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 2)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 4)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 6)..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 8)..], 1025);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 10)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 12)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 14)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 16)..], 3072);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 18)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 20)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 22)..], (ushort)epsg);

        for (var p = 0; p < pixelDataLength; p++)
        {
            span[pixelDataOffset + p] = (byte)(p % 251);
        }

        return buffer;
    }

    /// <summary>
    /// Builds a georeferenced GeoTIFF whose pixels are split across TWO strips, so
    /// the all-segments storage validation can be exercised.
    /// <paramref name="truncateAfterFirstStrip"/> drops the second strip's bytes
    /// while leaving both declared in the tags.
    /// </summary>
    private static byte[] BuildMultiStripGeoTiff(
        int width,
        int height,
        double originX,
        double originY,
        double pixelSize,
        int epsg,
        bool truncateAfterFirstStrip)
    {
        // width, height, stripOffsets(2), stripByteCounts(2), pixelScale,
        // tiepoint, geokeys
        const int entryCount = 7;
        var ifdOffset = 8;
        var dataStart = ifdOffset + 2 + (entryCount * 12) + 4;

        var offsetsArray = dataStart;              // 2 LONGs
        var byteCountsArray = offsetsArray + 8;    // 2 LONGs
        var pixelScaleOffset = byteCountsArray + 8;
        var tiepointOffset = pixelScaleOffset + 24;
        var geoKeyOffset = tiepointOffset + 48;
        var stripBytes = width * height / 2;
        var strip0Offset = geoKeyOffset + 24;
        var strip1Offset = strip0Offset + stripBytes;
        var totalLength = truncateAfterFirstStrip
            ? strip1Offset
            : strip1Offset + stripBytes;

        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();

        span[0] = 0x49;
        span[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[ifdOffset..], entryCount);

        var entry = ifdOffset + 2;
        WriteLongEntry(span, ref entry, tag: 256, value: (uint)width);
        WriteLongEntry(span, ref entry, tag: 257, value: (uint)height);
        WriteOffsetEntry(span, ref entry, tag: 273, type: 4, count: 2, offset: (uint)offsetsArray);
        WriteOffsetEntry(span, ref entry, tag: 279, type: 4, count: 2, offset: (uint)byteCountsArray);
        WriteOffsetEntry(span, ref entry, tag: 33550, type: 12, count: 3, offset: (uint)pixelScaleOffset);
        WriteOffsetEntry(span, ref entry, tag: 33922, type: 12, count: 6, offset: (uint)tiepointOffset);
        WriteOffsetEntry(span, ref entry, tag: 34735, type: 3, count: 12, offset: (uint)geoKeyOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(span[offsetsArray..], (uint)strip0Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offsetsArray + 4)..], (uint)strip1Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[byteCountsArray..], (uint)stripBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(byteCountsArray + 4)..], (uint)stripBytes);

        BinaryPrimitives.WriteDoubleLittleEndian(span[pixelScaleOffset..], pixelSize);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(pixelScaleOffset + 8)..], pixelSize);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(tiepointOffset + 24)..], originX);
        BinaryPrimitives.WriteDoubleLittleEndian(span[(tiepointOffset + 32)..], originY);

        BinaryPrimitives.WriteUInt16LittleEndian(span[geoKeyOffset..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 2)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 4)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 6)..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 8)..], 1025);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 10)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 12)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 14)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 16)..], 3072);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 18)..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 20)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 22)..], (ushort)epsg);

        for (var p = strip0Offset; p < totalLength; p++)
        {
            span[p] = (byte)(p % 251);
        }

        return buffer;
    }

    private static void WriteLongEntry(Span<byte> span, ref int entry, ushort tag, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[entry..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(entry + 2)..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], value);
        entry += 12;
    }

    private static void WriteOffsetEntry(
        Span<byte> span, ref int entry, ushort tag, ushort type, uint count, uint offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[entry..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(entry + 2)..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], offset);
        entry += 12;
    }

    /// <summary>
    /// Deterministic HTTP stub standing in for the cloud inference endpoint;
    /// captures the outgoing body and Authorization header for assertions.
    /// </summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public string? LastRequestBody { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();
            return _responder(request);
        }
    }

    /// <summary>Simple mutable holder for the published artifact URI.</summary>
    private sealed class CapturedArtifact(string? value)
    {
        public string? Value { get; set; } = value;
    }
}
