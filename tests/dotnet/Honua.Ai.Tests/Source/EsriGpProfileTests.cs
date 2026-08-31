// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Tools.EsriGp;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class EsriGpProfileTests
{
    [UnitTest]
    public void AddMcpDataAccessSurface_EsriGpProfileSwitch_ControlsExactToolFamily()
    {
        RegisteredNames(new ConfigurationBuilder().Build()).Should().NotContain(name => name.StartsWith("honua_esri_gp_", StringComparison.Ordinal));
        var enabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mcp:Profiles:0"] = "esri-gp" }).Build();
        RegisteredNames(enabled).Where(name => name.StartsWith("honua_esri_gp_", StringComparison.Ordinal))
            .Should().BeEquivalentTo(EsriGpToolNames.ListTasks, EsriGpToolNames.DescribeTask, EsriGpToolNames.ExecuteTask);
    }

    [UnitTest]
    public async Task DescribeTask_BufferAlias_ReturnsSdkContractFields()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var tool = new EsriGpDescribeTaskTool(jobs, new BuiltInProcessCatalog());
        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), McpTestFactory.ParseJson("""{"taskName":"Buffer"}"""), CancellationToken.None);
        var body = result.StructuredContent!.Value;
        body.GetProperty("taskName").GetString().Should().Be("Buffer");
        body.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        body.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Contain(["wkb", "srid", "distance"]);
    }

    [UnitTest]
    public async Task ExecuteTask_SdkJourneyFeatureSet_SubmitsCanonicalGovernedJob()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var now = DateTimeOffset.UtcNow;
        jobs.SubmitJobAsync(Arg.Any<AnalysisPlan>(), "journey-key", Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "gp-job-1",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new ExecutionJobSpec { Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob, Backend = "local", WorkloadName = "buffer" }
            });
        var tool = new EsriGpExecuteTaskTool(jobs, new BuiltInProcessCatalog());
        var arguments = McpTestFactory.ParseJson("""
            {"serviceId":"analysis","taskName":"Buffer","parameters":{"wkb":{"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[{"attributes":{"parcel_id":"P-101"},"geometry":{"rings":[[[-157.8616,21.3067],[-157.8608,21.3067],[-157.8608,21.3073],[-157.8616,21.3073],[-157.8616,21.3067]]]}}]},"distance":0.00025},"idempotencyKey":"journey-key"}
            """);
        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);
        result.StructuredContent!.Value.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        await jobs.Received(1).SubmitJobAsync(
            Arg.Is<AnalysisPlan>(plan => plan.Steps.Single().ProcessId == "geometry.buffer"
                && plan.Steps.Single().Inputs["srid"] == "4326"
                && Convert.FromBase64String(plan.Steps.Single().Inputs["wkb"]).Length > 20),
            "journey-key", Arg.Any<ClaimsPrincipal>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(metadata => metadata[GeoprocessingProtocolMetadataKeys.GPServerServiceId] == "analysis"),
            Arg.Any<CancellationToken>());
    }

    private static string[] RegisteredNames(IConfiguration configuration)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        services.AddMcpDataAccessSurface(configuration);
        return services.Where(d => d.ServiceType == typeof(IMcpTool)).Select(d => d.ImplementationType)
            .Where(type => type is not null).Select(type => type == typeof(EsriGpListTasksTool) ? EsriGpToolNames.ListTasks
                : type == typeof(EsriGpDescribeTaskTool) ? EsriGpToolNames.DescribeTask
                : type == typeof(EsriGpExecuteTaskTool) ? EsriGpToolNames.ExecuteTask : type!.Name).ToArray();
    }
}
