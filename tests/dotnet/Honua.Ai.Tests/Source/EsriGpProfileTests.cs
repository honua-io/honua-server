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
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        body.GetProperty("parameters").EnumerateArray()
            .Should().Contain(parameter => parameter.GetProperty("direction").GetString() == "esriGPParameterDirectionOutput"
                && parameter.GetProperty("name").GetString() == "outputFeatureLayer");
    }

    [UnitTest]
    public void BuildPlan_SameIdempotencyKey_UsesStablePlanId()
    {
        var definition = new BuiltInProcessCatalog().GetProcess("geometry.buffer")!;
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["wkb"] = "AQI=", ["distance"] = "1" };

        var first = EsriGpProjection.BuildPlan("analysis", "Buffer", definition, inputs, "same-key");
        var second = EsriGpProjection.BuildPlan("analysis", "Buffer", definition, inputs, "same-key");

        second.PlanId.Should().Be(first.PlanId);
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
        var tool = new EsriGpExecuteTaskTool(jobs, new BuiltInProcessCatalog(), new GPServerEsriInputTranslator(), NullLogger<EsriGpExecuteTaskTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"serviceId":"analysis","taskName":"Buffer","parameters":{"wkb":{"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[{"attributes":{"parcel_id":"P-101"},"geometry":{"rings":[[[-157.8616,21.3067],[-157.8608,21.3067],[-157.8608,21.3073],[-157.8616,21.3073],[-157.8616,21.3067]]]}}]},"distance":0.00025},"idempotencyKey":"journey-key"}
            """);
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("analysis", "GPServer", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "analysis", Name = "analysis" },
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            }));
        var access = Substitute.For<IAccessPolicyEvaluator>();
        access.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy?>(), Arg.Any<AccessPolicy?>(), Arg.Any<object?>())
            .Returns(Honua.Core.Features.Security.Domain.AccessDecision.Allowed());
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(validator);
            services.AddSingleton(access);
        });
        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);
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
