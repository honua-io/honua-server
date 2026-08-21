// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpEsriGpProfileTests
{
    [UnitTest]
    public void EsriGpTools_AreOptIn()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var catalog = new BuiltInProcessCatalog();
        IMcpTool[] tools =
        [
            new ListEsriGpTasksTool(jobs, catalog, NullLogger<ListEsriGpTasksTool>.Instance),
            new DescribeEsriGpTaskTool(jobs, catalog, NullLogger<DescribeEsriGpTaskTool>.Instance),
            new ExecuteEsriGpTaskTool(jobs, catalog, NullLogger<ExecuteEsriGpTaskTool>.Instance)
        ];

        var baseSurface = new McpDataAccessSurface(
            tools, [], NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions()));
        var esriSurface = new McpDataAccessSurface(
            tools, [], NullLogger<McpDataAccessSurface>.Instance,
            options: Options.Create(new McpOptions { Profiles = ["base", "esri-gp"] }));

        baseSurface.ToolNames.Should().BeEmpty();
        esriSurface.ToolNames.Should().BeEquivalentTo(tools.Select(tool => tool.Name));
    }

    [UnitTest]
    public async Task ListAndDescribe_BufferAlias_UseCanonicalGpServerProjection()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var catalog = new BuiltInProcessCatalog();
        var list = new ListEsriGpTasksTool(jobs, catalog, NullLogger<ListEsriGpTasksTool>.Instance);
        var describe = new DescribeEsriGpTaskTool(jobs, catalog, NullLogger<DescribeEsriGpTaskTool>.Instance);

        var listResult = await list.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), null, CancellationToken.None);
        using var arguments = JsonDocument.Parse("""{"taskName":"Buffer"}""");
        var describeResult = await describe.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments.RootElement, CancellationToken.None);

        var listedBuffer = listResult.StructuredContent!.Value.GetProperty("tasks")
            .EnumerateArray().Single(task => task.GetProperty("taskName").GetString() == "Buffer");
        listedBuffer.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        listedBuffer.GetProperty("isAlias").GetBoolean().Should().BeTrue();
        var description = describeResult.StructuredContent!.Value;
        description.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        description.GetProperty("supportsSynchronousExecution").GetBoolean().Should().BeTrue();
        description.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "wkb")
            .GetProperty("dataType").GetString().Should().Be("GPDataFile");
    }

    [Theory]
    [InlineData("Buffer", "{\"wkb\":{\"features\":[{\"geometry\":{\"x\":-157.8,\"y\":21.3},\"attributes\":{}}],\"geometryType\":\"esriGeometryPoint\",\"spatialReference\":{\"wkid\":4326}},\"distance\":10}", "geometry.buffer", "outputFeatureLayer")]
    [InlineData("Project", "{\"layerId\":0,\"targetSrid\":3857}", "conversion.feature-project", "outputFeatureLayer")]
    public async Task ExecuteTask_UsesGpServerCatalogPlanAndResultBindings(
        string taskName,
        string parametersJson,
        string expectedProcessId,
        string expectedOutputName)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        AnalysisPlan? submittedPlan = null;
        IReadOnlyDictionary<string, string>? submittedMetadata = null;
        jobs.SubmitJobAsync(
                Arg.Do<AnalysisPlan>(plan => submittedPlan = plan),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<IReadOnlyDictionary<string, string>?>(metadata => submittedMetadata = metadata),
                Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob());
        var tool = new ExecuteEsriGpTaskTool(
            jobs, new BuiltInProcessCatalog(), NullLogger<ExecuteEsriGpTaskTool>.Instance);
        using var arguments = JsonDocument.Parse(
            $$"""{"serviceId":"analysis","taskName":"{{taskName}}","parameters":{{parametersJson}}}""");
        var (context, _) = CreateExecuteContext();

        var result = await tool.InvokeAsync(
            context, arguments.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        submittedPlan.Should().NotBeNull();
        submittedPlan!.Steps.Should().ContainSingle();
        submittedPlan.Steps[0].ProcessId.Should().Be(expectedProcessId);
        if (taskName == "Buffer")
        {
            submittedPlan.Steps[0].Inputs["wkb"].Should().NotContain("features");
            Convert.FromBase64String(submittedPlan.Steps[0].Inputs["wkb"]).Should().NotBeEmpty();
            submittedPlan.Steps[0].Inputs.Should().Contain("srid", "4326");
        }
        submittedPlan.Outputs.Should().Equal(ArtifactKind.FeatureLayer);
        var (violations, _) = ProcessPlanValidator.Validate(submittedPlan, new BuiltInProcessCatalog());
        violations.Should().BeEmpty();
        submittedMetadata.Should().Contain("submittedVia", "GPServer");
        submittedMetadata.Should().Contain("gpserver.serviceId", "analysis");
        submittedMetadata.Should().Contain("gpserver.taskName", taskName);
        submittedMetadata.Should().Contain("gpserver.output.0", expectedOutputName);
        result.StructuredContent!.Value.GetProperty("processId").GetString().Should().Be(expectedProcessId);
        result.StructuredContent!.Value.GetProperty("resourceUri").GetString().Should().Be("honua://jobs/esri-gp-job-1");
    }

    [UnitTest]
    public async Task ExecuteTask_MultiFeatureEsriPayload_ReturnsHonestCapabilityError()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var tool = new ExecuteEsriGpTaskTool(
            jobs, new BuiltInProcessCatalog(), NullLogger<ExecuteEsriGpTaskTool>.Instance);
        using var arguments = JsonDocument.Parse("""
            {
              "serviceId": "analysis",
              "taskName": "Buffer",
              "parameters": {
                "wkb": {
                  "features": [
                    {"geometry":{"x":-157.8,"y":21.3}},
                    {"geometry":{"x":-157.7,"y":21.4}}
                  ],
                  "spatialReference": {"wkid":4326}
                },
                "distance": 10
              }
            }
            """);
        var (context, _) = CreateExecuteContext();

        var action = () => tool.InvokeAsync(
            context, arguments.RootElement, CancellationToken.None);

        await action.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*FeatureSet carrying 2 features*");
        await jobs.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default, default!, default, default);
    }

    [UnitTest]
    public async Task ExecuteTask_NonexistentGpServerService_ReturnsNotFoundWithoutSubmitting()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var tool = new ExecuteEsriGpTaskTool(
            jobs, new BuiltInProcessCatalog(), NullLogger<ExecuteEsriGpTaskTool>.Instance);
        var serviceResult = ResourceValidationResult.NotFound<MetadataV2Service>(
            "Service 'missing' was not found.");
        var (context, resourceValidator) = CreateExecuteContext(serviceResult);
        using var arguments = JsonDocument.Parse("""
            {
              "serviceId": "missing",
              "taskName": "Buffer",
              "parameters": {"wkb":"AQ==","distance":10}
            }
            """);

        var action = () => tool.InvokeAsync(
            context, arguments.RootElement, CancellationToken.None);

        await action.Should().ThrowAsync<GeoprocessingNotFoundException>()
            .WithMessage("Service 'missing' was not found.");
        await resourceValidator.Received(1).ValidateServiceV2Async(
            "missing", ServiceProtocols.GPServer, Arg.Any<CancellationToken>());
        await jobs.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default, default!, default, default);
    }

    [UnitTest]
    public async Task ExecuteTask_DeniedGpServerService_ReturnsAuthorizationFailureWithoutSubmitting()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var tool = new ExecuteEsriGpTaskTool(
            jobs, new BuiltInProcessCatalog(), NullLogger<ExecuteEsriGpTaskTool>.Instance);
        var (context, _) = CreateExecuteContext(
            accessDecision: AccessDecision.Forbidden("restricted"));
        using var arguments = JsonDocument.Parse("""
            {
              "serviceId": "analysis",
              "taskName": "Buffer",
              "parameters": {"wkb":"AQ==","distance":10}
            }
            """);

        var action = () => tool.InvokeAsync(
            context, arguments.RootElement, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        exception.Which.RequiresAuthentication.Should().BeFalse();
        exception.Which.Message.Should().Contain("GPServer service 'analysis'");
        await jobs.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default, default!, default, default);
    }

    private static (DefaultHttpContext Context, IResourceValidator ResourceValidator)
        CreateExecuteContext(
            ResourceValidationResult<MetadataV2Service>? serviceResult = null,
            AccessDecision? accessDecision = null)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "analysis", Name = "analysis" },
            Route = "/rest/services/analysis/GPServer",
            Protocols = [ServiceProtocols.GPServer]
        };
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async(
                Arg.Any<string>(), ServiceProtocols.GPServer, Arg.Any<CancellationToken>())
            .Returns(serviceResult ?? ResourceValidationResult.Success(service));

        var accessEvaluator = Substitute.For<IAccessPolicyEvaluator>();
        accessEvaluator.Evaluate(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AccessPolicy?>(),
                Arg.Any<AccessPolicy?>(),
                Arg.Any<object?>())
            .Returns(accessDecision ?? AccessDecision.Allowed());

        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(resourceValidator);
            services.AddSingleton(accessEvaluator);
        });
        return (context, resourceValidator);
    }

    private static ExecutionJobRecord CreateQueuedJob()
        => new()
        {
            OperationId = "esri-gp-job-1",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "esri-gp",
                Parameters = new Dictionary<string, string>()
            }
        };
}
