// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Geoprocessing.Inference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Fact]
    public async Task Classify_ConfiguredHttpModel_PreservesGridAndMatchesThreeClassConfusionOracle()
    {
        var backend = Path.Join(AppContext.BaseDirectory, "Fixtures", "ImageryClassifier");
        await using var container = new ContainerBuilder(Image)
            .WithBindMount(backend, "/app", AccessMode.ReadOnly)
            .WithPortBinding(8080, true)
            .WithEntrypoint("python3")
            .WithCommand("/app/server.py", "/app/model.json")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/health")))
            .Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await container.StartAsync(timeout.Token);

        var services = new ServiceCollection();
        services.AddHttpClient(HttpImageryInferenceClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.Configure<ImageryInferenceOptions>(options =>
        {
            options.Provider = "http";
            options.Endpoint = $"http://127.0.0.1:{container.GetMappedPublicPort(8080)}/infer";
        });
        services.Configure<GeoprocessingExecutorOptions>(_ => { });
        await using var provider = services.BuildServiceProvider();
        var adapter = new HttpImageryInferenceClient(provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<HttpImageryInferenceClient>.Instance);
        var executor = new ImageryInferenceJobExecutor(provider.GetRequiredService<IOptionsMonitor<ImageryInferenceOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(), [adapter],
            NullLogger<ImageryInferenceJobExecutor>.Instance);
        var job = GdalJobFactory.Job("imagery.classify", ("source", Input("grid.tif")),
            ("model", "honua-proof-three-centroids-v1"), ("task", "classification"));
        var context = new RecordingJobExecutionContext(job.OperationId);
        var result = await executor.ExecuteAsync(job, context, timeout.Token);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        context.Artifacts.Should().ContainSingle().Which.Should().StartWith(ImageryInferenceJobExecutor.GeoTiffDataUriPrefix);
        var output = await Decode(GdalCli.DecodeDataUri(context.Artifacts.Single()));
        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 1);
        var metadata = output.GetProperty("metadata");
        metadata.GetProperty("HONUA_MODEL_ID").GetString().Should().Be("honua-proof-three-centroids-v1");
        metadata.GetProperty("HONUA_MODEL_SHA256").GetString().Should()
            .Be(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Join(backend, "model.json"), timeout.Token))));
        metadata.GetProperty("HONUA_CLASSIFIER").GetString().Should().Be("nearest-centroid-squared-euclidean-v1");
        // In this two-band scene, band 2 = 10 * band 1. The centroids have band-1
        // values 2, 8, 14, so the independently derived boundaries are 5 and 11.
        // Ties at 5 and 11 select the earlier class. One source pixel is nodata.
        double[] expected = [11, 11, 11, 11, 11, 255, 29, 29, 29, 29, 29, 47, 47, 47, 47, 47];
        AssertBand(output, 0, expected, "Byte", 255, 0);
        var values = output.GetProperty("bands")[0].GetProperty("values").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        int[] classes = [11, 29, 47];
        var confusion = new int[3, 3];
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != 255)
            {
                confusion[Array.IndexOf(classes, (int)expected[i]), Array.IndexOf(classes, values[i])]++;
            }
        }
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                confusion[row, column].Should().Be(row == column ? 5 : 0);
            }
        }
    }
}
