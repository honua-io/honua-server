// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Geoprocessing.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Execution-content proof for <c>imagery.classify</c> (#3935).
///
/// <para>
/// The prior evidence exercised validation and artifact handling against a
/// simulated backend that echoed bytes, so nothing proved a model had ever
/// classified anything. These cases stand up a REAL model server — a loopback
/// HTTP endpoint speaking Honua's own inference contract, exactly the deployment
/// shape the <c>http</c> provider documents — which runs a real
/// minimum-distance-to-mean classifier (NumPy) over the posted scene inside the
/// pinned production GDAL image and returns a GDAL-written classification
/// GeoTIFF. Honua's production executor and HTTP adapter carry the whole
/// exchange; nothing is substituted on the Honua side.
/// </para>
/// <para>
/// The oracle is FROZEN, not a snapshot of the run: the committed scene's band
/// values and the committed class centroids are recorded in
/// <c>TestData/ImageryClassify/generate-scene.py</c>, and the expected class map
/// and per-class counts below are those literals carried forward. A run that
/// classified nothing, classified with the wrong model, or misaligned the output
/// grid fails them.
/// </para>
/// </summary>
[Trait("Category", "ImageryClassifyExecutionProof")]
public sealed class ImageryClassifyExecutionProofTests : IAsyncLifetime
{
    /// <summary>Model reference the proof asks the backend to run.</summary>
    private const string ModelReference = "honua-mindist-v1";

    /// <summary>A second committed model whose class ids are rotated.</summary>
    private const string TransposedModelReference = "honua-mindist-transposed";

    /// <summary>
    /// Frozen class map, row-major from the north-west corner. Derived from the
    /// committed scene layout (water/vegetation/soil) documented in the fixture
    /// generator, not captured from a run.
    /// </summary>
    private static readonly int[] ExpectedClasses =
    [
        1, 1, 2, 2,
        1, 1, 2, 2,
        3, 3, 3, 2,
        3, 3, 1, 1,
    ];

    /// <summary>Frozen class histogram: the confusion oracle's diagonal.</summary>
    private static readonly Dictionary<int, int> ExpectedClassCounts =
        new() { [1] = 6, [2] = 5, [3] = 5 };

    // Committed scene georeferencing: origin (10, 20), 0.5 degree cells, EPSG:4326.
    private static readonly double[] ExpectedTransform = [10.0, 0.5, 0.0, 20.0, 0.0, -0.5];

    private MinimumDistanceModelServer _backend = null!;

    public async Task InitializeAsync() => _backend = await MinimumDistanceModelServer.StartAsync();

    public async Task DisposeAsync() => await _backend.DisposeAsync();

    [Fact]
    public async Task Classify_RealSceneThroughARealModelServer_MatchesTheFrozenClassOracle()
    {
        var scene = await File.ReadAllBytesAsync(Fixture("classify-scene.tif"));
        var executor = CreateExecutor(_backend.Endpoint);
        var job = CreateJobRecord(scene, ModelReference);
        var context = CreateContext(job.OperationId, out var published);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

        // Model identity: the backend selected and ran the model the job named.
        _backend.LastModel.Should().Be(ModelReference);
        _backend.LastTask.Should().Be("classification");
        _backend.LastSourceCrs.Should().Be(4326, "the adapter forwards the scene CRS so the backend can georeference");
        _backend.LastRanModelId.Should().Be(ModelReference, "the inference program reports the model it loaded");

        var classified = await DecodeAsync(published.Value!);

        // Source/output grid alignment and CRS.
        classified.GetProperty("width").GetInt32().Should().Be(4);
        classified.GetProperty("height").GetInt32().Should().Be(4);
        classified.GetProperty("bands").GetInt32().Should().Be(1, "a classification is one label band");
        classified.GetProperty("epsg").GetInt32().Should().Be(4326);
        classified.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(ExpectedTransform);

        AssertClassMap(classified, ExpectedClasses);
    }

    [Fact]
    public async Task ClassifyOracle_ADifferentCommittedModel_IsRejectedEvenThoughTheOutputIsWellFormed()
    {
        // A plausible wrong-but-well-formed classification: the same scene, the same
        // real classifier and the same grid, but a committed model whose class ids
        // are rotated. Only the frozen class oracle catches it — the artifact is a
        // valid, georeferenced, single-band GeoTIFF carrying legal class ids.
        var scene = await File.ReadAllBytesAsync(Fixture("classify-scene.tif"));
        var executor = CreateExecutor(_backend.Endpoint);
        var job = CreateJobRecord(scene, TransposedModelReference);
        var context = CreateContext(job.OperationId, out var published);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        var classified = await DecodeAsync(published.Value!);

        classified.GetProperty("epsg").GetInt32().Should().Be(4326);
        classified.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(ExpectedTransform, "the substituted output is aligned, not corrupt");
        Values(classified).Should().OnlyContain(value => value == 1 || value == 2 || value == 3,
            "every emitted label is a legal class id");

        Action assert = () => AssertClassMap(classified, ExpectedClasses);
        assert.Should().Throw<XunitException>("the frozen class oracle must reject a different model's output");
    }

    [Fact]
    public async Task Classify_UnknownModelReference_FailsWithoutPublishingAnArtifact()
    {
        var scene = await File.ReadAllBytesAsync(Fixture("classify-scene.tif"));
        var executor = CreateExecutor(_backend.Endpoint);
        var job = CreateJobRecord(scene, "no-such-model");
        var context = CreateContext(job.OperationId, out var published);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        published.Value.Should().BeNull("a backend rejection must not publish a classification");
    }

    // -------------------------------------------------------------------------
    // Oracle
    // -------------------------------------------------------------------------

    private static void AssertClassMap(JsonElement classified, int[] expected)
    {
        var actual = Values(classified);
        actual.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            actual[index].Should().Be(expected[index],
                $"pixel {index} (row {index / 4}, column {index % 4}) classification");
        }

        foreach (var (classId, count) in ExpectedClassCounts)
        {
            actual.Count(value => value == classId).Should().Be(count, $"class {classId} pixel count");
        }
    }

    private static int[] Values(JsonElement classified) =>
        classified.GetProperty("values")[0].EnumerateArray().Select(value => value.GetInt32()).ToArray();

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    private static string Fixture(string name) =>
        Path.Join(AppContext.BaseDirectory, "TestData", "ImageryClassify", name);

    private static async Task<JsonElement> DecodeAsync(string artifactUri)
    {
        const string Prefix = "data:";
        artifactUri.Should().StartWith(Prefix);
        var comma = artifactUri.IndexOf(',', StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(artifactUri[(comma + 1)..]);

        var scratch = Directory.CreateTempSubdirectory("honua-classify-proof");
        try
        {
            await File.WriteAllBytesAsync(Path.Join(scratch.FullName, "classified.tif"), bytes);
            File.Copy(Fixture("decode-classified.py"), Path.Join(scratch.FullName, "decode.py"), overwrite: true);
            var json = await PinnedGdal.RunAsync(scratch.FullName, ["python3", "decode.py", "classified.tif"]);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static ImageryInferenceJobExecutor CreateExecutor(string endpoint)
    {
        var inferenceMonitor = Substitute.For<IOptionsMonitor<ImageryInferenceOptions>>();
        inferenceMonitor.CurrentValue.Returns(new ImageryInferenceOptions
        {
            Provider = HttpImageryInferenceClient.ProviderId,
            Endpoint = endpoint,
            TimeoutSeconds = 300
        });

        var executorMonitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        executorMonitor.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        });

        // A real HttpClient over a real loopback socket: the adapter's transport is
        // not substituted either.
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        return new ImageryInferenceJobExecutor(
            inferenceMonitor,
            executorMonitor,
            [new HttpImageryInferenceClient(httpClientFactory, NullLogger<HttpImageryInferenceClient>.Instance)],
            NullLogger<ImageryInferenceJobExecutor>.Instance);
    }

    private static ExecutionJobRecord CreateJobRecord(byte[] scene, string model)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        return new ExecutionJobRecord
        {
            OperationId = "op-imagery-classify-proof",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:imagery.classify",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] =
                        ImageryInferenceJobExecutor.HandledProcessId,
                    ["protocolProcessId"] = ImageryInferenceJobExecutor.HandledProcessId,
                    [prefix + "source"] = Convert.ToBase64String(scene),
                    [prefix + "model"] = model,
                    [prefix + "task"] = "classification"
                }
            }
        };
    }

    private static IJobExecutionContext CreateContext(string operationId, out CapturedArtifact published)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns(operationId);
        var box = new CapturedArtifact();
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => box.Value = call.ArgAt<string>(0));
        published = box;
        return context;
    }

    private sealed class CapturedArtifact
    {
        public string? Value { get; set; }
    }

    /// <summary>
    /// A real model server: a loopback HTTP endpoint speaking Honua's inference
    /// contract that runs the committed minimum-distance classifier inside the
    /// pinned production GDAL image. This is the deployment shape the <c>http</c>
    /// provider documents (a model server, or a thin gateway in front of one), so
    /// Honua's side of the exchange is entirely production code.
    /// </summary>
    private sealed class MinimumDistanceModelServer : IAsyncDisposable
    {
        private static readonly Dictionary<string, string> Models =
            new(StringComparer.Ordinal)
            {
                [ModelReference] = "min-distance-model.json",
                [TransposedModelReference] = "transposed-model.json",
            };

        private readonly WebApplication _app;
        private readonly ServerState _state;

        private MinimumDistanceModelServer(WebApplication app, ServerState state, string endpoint)
        {
            _app = app;
            _state = state;
            Endpoint = endpoint;
        }

        public string Endpoint { get; }

        public string? LastModel => _state.Model;

        public string? LastTask => _state.Task;

        public int? LastSourceCrs => _state.SourceCrs;

        public string? LastRanModelId => _state.RanModelId;

        public static async Task<MinimumDistanceModelServer> StartAsync()
        {
            var state = new ServerState();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();

            Func<HttpContext, Task<IResult>> handler = http => HandleAsync(http, state);
            app.MapPost("/infer", handler);

            await app.StartAsync();
            var address = app.Urls.First().TrimEnd('/');
            return new MinimumDistanceModelServer(app, state, address + "/infer");
        }

        private static async Task<IResult> HandleAsync(HttpContext http, ServerState state)
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var root = document.RootElement;
            var model = root.GetProperty("model").GetString()!;
            state.Model = model;
            state.Task = root.GetProperty("task").GetString();
            state.SourceCrs = root.TryGetProperty("sourceCrs", out var crs) && crs.ValueKind == JsonValueKind.Number
                ? crs.GetInt32()
                : null;

            if (!Models.TryGetValue(model, out var modelFile))
            {
                return Results.NotFound(new { error = "unknown model" });
            }

            var scratch = Directory.CreateTempSubdirectory("honua-model-server");
            try
            {
                await File.WriteAllBytesAsync(
                    Path.Join(scratch.FullName, "scene.tif"),
                    Convert.FromBase64String(root.GetProperty("image").GetString()!),
                    http.RequestAborted);
                foreach (var asset in new[] { modelFile, "min_distance_inference.py" })
                {
                    File.Copy(Fixture(asset), Path.Join(scratch.FullName, asset), overwrite: true);
                }

                var stdout = await PinnedGdal.RunAsync(scratch.FullName,
                    ["python3", "min_distance_inference.py", modelFile, "scene.tif", "classified.tif"]);
                using var report = JsonDocument.Parse(stdout);
                state.RanModelId = report.RootElement.GetProperty("model").GetString();

                var classified = await File.ReadAllBytesAsync(
                    Path.Join(scratch.FullName, "classified.tif"), http.RequestAborted);
                return Results.Text(
                    $$"""{"outputType":"raster","raster":"{{Convert.ToBase64String(classified)}}"}""",
                    "application/json");
            }
            finally
            {
                scratch.Delete(recursive: true);
            }
        }

        private sealed class ServerState
        {
            public string? Model { get; set; }

            public string? Task { get; set; }

            public int? SourceCrs { get; set; }

            public string? RanModelId { get; set; }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs a command in the digest-pinned production GDAL image with a host
    /// directory mounted. The proof consumes the same immutable native dependency
    /// the worker ships, so a drifted local GDAL cannot change a result.
    /// </summary>
    private static class PinnedGdal
    {
        private static readonly string Image = ReadProductionImage();

        public static async Task<string> RunAsync(string workspace, string[] command)
        {
            var arguments = new List<string>
            {
                "run", "--rm", "--network", "none",
                "-v", workspace + ":/proof", "-w", "/proof",
                "--entrypoint", command[0], Image,
            };
            arguments.AddRange(command[1..]);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);

            var output = await stdout;
            process.ExitCode.Should().Be(0,
                $"'{string.Join(' ', command)}' must succeed in the pinned GDAL image: {await stderr}");
            return output;
        }

        private static string ReadProductionImage()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
            {
                root = root.Parent;
            }

            root.Should().NotBeNull("the proof must consume the production worker's native dependency pin");
            const string Prefix = "ARG GDAL_BASE_IMAGE=";
            var image = File.ReadLines(Path.Join(root!.FullName, "docker", "worker-gdal", "Dockerfile"))
                .Single(line => line.StartsWith(Prefix, StringComparison.Ordinal))[Prefix.Length..];
            image.Should().Contain("@sha256:", "native evidence must use an immutable tool image");
            return image;
        }
    }
}
