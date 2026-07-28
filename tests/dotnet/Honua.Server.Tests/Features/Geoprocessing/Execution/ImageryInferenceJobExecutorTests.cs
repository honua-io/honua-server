// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    // Minimal little-endian TIFF magic + trailing payload; the executor only
    // validates the magic, so this stands in for a fixture GeoTIFF scene.
    private static readonly byte[] SourceGeoTiff =
        [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44];

    // A distinct "classification map" GeoTIFF the fake backend answers with. The
    // executor must land these bytes UNMODIFIED, which is what preserves the
    // backend-emitted georeferencing (extent/CRS) in the artifact.
    private static readonly byte[] ClassifiedGeoTiff =
        [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x55, 0x66, 0x77, 0x88, 0x99];

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
        long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var inferenceOptions = new ImageryInferenceOptions
        {
            Provider = provider,
            Endpoint = "https://inference.example.com/v1/infer",
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
