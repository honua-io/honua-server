// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
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
        const string featureCollection =
            """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[10.0,20.0]},"properties":{"class":"building","score":0.91}}]}""";
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
        double tiepointJ = 0)
    {
        // Entries: ImageWidth, ImageLength [, ModelPixelScale, ModelTiepoint, GeoKeyDirectory]
        var entryCount = georeferenced ? 5 : 2;
        var ifdOffset = 8;
        var ifdSize = 2 + (entryCount * 12) + 4;
        var dataStart = ifdOffset + ifdSize;

        var pixelScaleOffset = dataStart;
        var tiepointOffset = pixelScaleOffset + 24;   // 3 doubles
        var geoKeyOffset = tiepointOffset + 48;       // 6 doubles
        var totalLength = georeferenced ? geoKeyOffset + 16 : dataStart;

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

        if (georeferenced)
        {
            WriteOffsetEntry(span, ref entry, tag: 33550, type: 12, count: 3, offset: (uint)pixelScaleOffset);
            WriteOffsetEntry(span, ref entry, tag: 33922, type: 12, count: 6, offset: (uint)tiepointOffset);
            WriteOffsetEntry(span, ref entry, tag: 34735, type: 3, count: 8, offset: (uint)geoKeyOffset);

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
            BinaryPrimitives.WriteUInt16LittleEndian(span[geoKeyOffset..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 2)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 4)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 6)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 8)..], 3072);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 10)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 12)..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(geoKeyOffset + 14)..], (ushort)epsg);
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
