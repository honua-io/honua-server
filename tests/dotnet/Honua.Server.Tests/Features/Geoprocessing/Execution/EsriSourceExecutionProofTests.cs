// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class EsriSourceExecutionProofTests
{
    [UnitTest]
    [Trait("Category", "LayerExecutionProof")]
    public async Task EsriSource_PagedFilteredFixture_PublishesEverySelectedFeatureExactlyOnce()
    {
        using var output = await ExecuteFixture("none");
        AssertSelected(output.RootElement);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("duplicate-page")]
    [InlineData("swapped-axes")]
    [InlineData("changed-value")]
    [Trait("Category", "LayerExecutionProof")]
    public async Task EsriSource_WellFormedWrongFixtureOutput_FailsSemanticOracle(string defect)
    {
        using var output = await ExecuteFixture(defect);
        Action assert = () => AssertSelected(output.RootElement);
        assert.Should().Throw<XunitException>($"{defect} must fail the output oracle");
    }

    private static async Task<JsonDocument> ExecuteFixture(string defect)
    {
        using var handler = new FeatureServerFixture(defect);
        using var client = new HttpClient(handler);
        var rest = new ArcGisRestClient(client, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var source = new EsriFeatureServerDagSource(rest, NullLogger<EsriFeatureServerDagSource>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<IDagFeatureSource>(source);
        await using var provider = services.BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = RemoteSourceExecutor.ForProcess("source.esri-featureserver",
            provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RemoteSourceExecutor>.Instance);
        var parameters = new Dictionary<string, string>
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "source.esri-featureserver",
            ["protocolProcessId"] = "source.esri-featureserver"
        };
        foreach (var (key, value) in new[] { ("serviceUrl", "https://fixture.example/FeatureServer"),
            ("esriLayerId", "7"), ("where", "status = 'active'"), ("since", "2026-01-01T00:00:00Z"),
            ("watermarkField", "edited"), ("pageSize", "2"), ("outSrid", "4326"),
            ("outFields", "OBJECTID,name,value") })
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
        }
        var job = new ExecutionJobRecord
        {
            OperationId = "esri-source-proof",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:source.esri-featureserver",
                Parameters = parameters
            }
        };
        var context = Substitute.For<IJobExecutionContext>();
        var artifacts = new List<string>();
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(c => artifacts.Add(c.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        artifacts.Should().ContainSingle();
        handler.Offsets.Should().Equal(0, 2);
        return JsonDocument.Parse(Convert.FromBase64String(artifacts[0][(artifacts[0].IndexOf(',') + 1)..]));
    }

    private static void AssertSelected(JsonElement output)
    {
        output.GetProperty("type").GetString().Should().Be("FeatureCollection");
        output.GetProperty("srid").GetInt32().Should().Be(4326);
        output.GetProperty("featureCount").GetInt32().Should().Be(3);
        var features = output.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(3);
        features.Select(f => f.GetProperty("properties").GetProperty("OBJECTID").GetDouble()).Should().Equal(11d, 13d, 15d);
        // Literal expected rows, independent of fixture serialization and conversion.
        (string? Name, double Value, double X, double Y)[] expected =
            [("station-11", 77, 11.25, -11.5), (null, 91, 13.25, -13.5), ("station-15", 105, 15.25, -15.5)];
        for (var i = 0; i < features.Length; i++)
        {
            features[i].GetProperty("type").GetString().Should().Be("Feature");
            var props = features[i].GetProperty("properties");
            props.GetProperty("name").GetString().Should().Be(expected[i].Name);
            props.GetProperty("value").GetDouble().Should().Be(expected[i].Value);
            props.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("OBJECTID", "name", "value");
            var geometry = features[i].GetProperty("geometry");
            geometry.GetProperty("type").GetString().Should().Be("Point");
            geometry.GetProperty("coordinates").EnumerateArray().Select(c => c.GetDouble()).Should().Equal(expected[i].X, expected[i].Y);
        }
    }

    private sealed class FeatureServerFixture(string defect) : HttpMessageHandler
    {
        public List<int> Offsets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/FeatureServer/7/query");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            query["where"].ToString().Should().Be("status = 'active' AND edited >= TIMESTAMP '2026-01-01T00:00:00Z'");
            query["outFields"].ToString().Should().Be("OBJECTID,name,value");
            query["outSR"].ToString().Should().Be("4326");
            query["resultRecordCount"].ToString().Should().Be("2");
            var offset = int.Parse(query["resultOffset"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Offsets.Add(offset);
            // The fixture's selected rows are specified independently of executor output.
            // Even IDs are inactive; row 9 predates the requested watermark.
            int[] selected = defect == "duplicate-page" ? [11, 13, 13] : [11, 13, 15];
            var features = selected.Skip(offset).Take(2).Select(id => new
            {
                attributes = new { OBJECTID = id, name = id == 13 ? null : "station-" + id, value = id * 7 + (defect == "changed-value" ? 1 : 0) },
                geometry = new
                {
                    x = defect == "swapped-axes" ? -id - 0.5 : id + 0.25,
                    y = defect == "swapped-axes" ? id + 0.25 : -id - 0.5
                }
            }).ToArray();
            var body = JsonSerializer.Serialize(new
            {
                features,
                exceededTransferLimit = offset + features.Length < selected.Length,
                geometryType = "esriGeometryPoint",
                spatialReference = new { wkid = 4326 }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
