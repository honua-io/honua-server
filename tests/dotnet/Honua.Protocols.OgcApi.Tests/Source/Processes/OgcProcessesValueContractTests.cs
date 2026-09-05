// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.Ogc.Api.Processes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Trait("Tier", "Fast")]
public sealed class OgcProcessesValueContractTests
{
    [Fact]
    public void DataUri_EscapedBase64_UsesDecodedSizeLimit()
    {
        ProcessEndpoints.TryDecodeDataUri("data:application/octet-stream;base64,AP8%3D", 2,
            out var payload, out var mediaType, out var error).Should().BeTrue(error);
        payload.Should().Equal(0, 255);
        mediaType.Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Results_SpilledFeatureStream_ReturnsGeoJsonValue(bool raw)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ogc-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "features.ndjson");
        const string feature = """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2,3]},"properties":{"name":"sample","__honua_srid":4326}}""";
        try
        {
            await File.WriteAllTextAsync(path, feature + "\n");
            var reference = FeatureStreamArtifact.BuildStreamReference(path, 1, Encoding.UTF8.GetByteCount(feature) + 1);
            var response = await RenderResultAsync(reference, root, 4096, raw);
            response.Status.Should().Be(200);
            using var json = JsonDocument.Parse(response.Body);
            var value = raw ? json.RootElement : json.RootElement.GetProperty("outputFeatureLayer").GetProperty("value");
            value.GetProperty("type").GetString().Should().Be("FeatureCollection");
            var output = value.GetProperty("features")[0];
            output.GetProperty("properties").GetProperty("name").GetString().Should().Be("sample");
            output.GetProperty("properties").TryGetProperty("__honua_srid", out _).Should().BeFalse();
            output.GetProperty("geometry").GetProperty("coordinates").GetArrayLength().Should().Be(3);
            response.Body.Should().NotContain(root);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Results_SpilledFeatureStream_EnforcesActualSizeAndRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ogc-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "features.ndjson");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 2048));
            var reference = FeatureStreamArtifact.BuildStreamReference(path, 1, 0);
            (await RenderResultAsync(reference, root, 1024, raw: false)).Status.Should().Be(413);
            var outside = await RenderResultAsync(reference, Path.Combine(root, "allowed"), 4096, raw: false);
            outside.Status.Should().Be(500);
            outside.Body.Should().NotContain(path);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task OpenApi_RawResults_DeclareNativeRasterFormats()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "src", "Honua.Server", "ogc-processes-openapi.json")));
        var resultsPath = json.RootElement.GetProperty("paths").EnumerateObject()
            .Single(path => path.Name.EndsWith("/results", StringComparison.Ordinal)).Value;
        var content = resultsPath.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content");
        foreach (var mediaType in new[] { "image/tiff", "image/png", "image/jpeg" })
        {
            content.GetProperty(mediaType).GetProperty("schema").GetProperty("format").GetString().Should().Be("binary");
        }
    }

    private static async Task<(int Status, string Body)> RenderResultAsync(string reference, string root, long maxBytes, bool raw)
    {
        await using var services = new ServiceCollection().AddLogging()
            .Configure<GeoprocessingExecutorOptions>(options =>
            {
                options.OutputRootDirectory = root;
                options.MaxArtifactBytes = maxBytes;
            }).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var job = new ExecutionJobRecord
        {
            OperationId = "stream-result",
            Spec = new ExecutionJobSpec { Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob, Backend = "local", WorkloadName = "transform.attribute-rename" }
        };
        var package = AnalysisResultPackage.CreateCompleted("stream-result:v1", new ResultSummary { Title = "stream output" },
            [new ArtifactRef { ArtifactId = "stream-artifact", Kind = ArtifactKind.FeatureLayer, Label = "outputFeatureLayer", Uri = reference }],
            [], new ProvenanceRecord { Sources = [], ProcessDefinitions = ["transform.attribute-rename"], ExecutedAt = DateTimeOffset.UtcNow });
        var result = raw
            ? await JobEndpoints.BuildRawResultsResponseAsync(job, context, package)
            : await JobEndpoints.BuildValueResultsResponseAsync(job, context, package);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }
}
