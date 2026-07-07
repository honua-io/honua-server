// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies the MCP <c>honua_publish_result</c> tool (honua-server#2482): it
/// resolves a completed analysis job's result package through the canonical
/// <see cref="IGeoprocessingJobService"/>, reads the selected artifact's
/// materialized-table coordinates, and routes the promotion through the very
/// same <c>service.publish</c> operation (<see cref="IOperationInvoker"/>) as
/// <c>honua_publish_service</c> — returning a structured handle whose
/// <c>serviceId</c> + <c>layerId</c> chain straight into
/// <c>honua_query_features</c>. Structured errors (job not terminal, unsupported
/// artifact kind, unauthenticated) surface through the shared exception channel.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class PublishResultToolTests
{
    private const string JobId = "job_7f3a2c";
    private const string ArtifactId = "artifact_parcels_within_500m";
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";

    private static DefaultHttpContext AuthenticatedContext(IOperationInvoker? invoker)
    {
        var services = new ServiceCollection();
        if (invoker is not null)
        {
            services.AddSingleton(invoker);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent-x")], "Test"))
        };
    }

    private static DefaultHttpContext AnonymousContext() => new()
    {
        RequestServices = new ServiceCollection().BuildServiceProvider()
    };

    private static ArtifactRef FeatureLayerArtifact(
        string artifactId = ArtifactId,
        bool withTableCoordinates = true) => new()
        {
            ArtifactId = artifactId,
            Kind = ArtifactKind.FeatureLayer,
            Label = "Parcels within 500m of flood zone",
            Uri = $"honua://analysis/artifacts/{artifactId}",
            ContentType = "application/geo+json",
            Metadata = withTableCoordinates
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["connectionId"] = ConnectionId,
                ["schema"] = "honua_data",
                ["table"] = "imported_parcels_within_500m",
                ["geometryColumn"] = "geometry",
                ["srid"] = "4326",
                ["primaryKey"] = "id",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
        };

    private static AnalysisResultPackage CompletedPackage(params ArtifactRef[] artifacts) =>
        AnalysisResultPackage.CreateCompleted(
            resultPackageId: "result_1",
            summary: new ResultSummary { Title = "Flood proximity analysis" },
            artifacts: artifacts,
            workspaceRefs: [],
            provenance: new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });

    private static IGeoprocessingJobService JobServiceReturning(AnalysisResultPackage package)
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(package));
        return jobService;
    }

    private static IGeoprocessingJobService JobServiceThrowing(Exception exception)
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AnalysisResultPackage>(exception));
        return jobService;
    }

    private static PublishResultTool CreateTool(IGeoprocessingJobService jobService)
        => new(jobService, NullLogger<PublishResultTool>.Instance);

    private static System.Text.Json.JsonElement Arguments(McpPublishResultArgument argument)
        => McpTestFactory.ToArguments(argument, McpJsonContext.Default.McpPublishResultArgument);

    [UnitTest]
    public void Describe_AdvertisesWriteAnnotations_ChainDocumentation_AndOutputSchema()
    {
        var descriptor = CreateTool(Substitute.For<IGeoprocessingJobService>()).Describe();

        descriptor.Name.Should().Be("honua_publish_result");
        descriptor.Title.Should().NotBeNullOrWhiteSpace();
        descriptor.Annotations.Should().NotBeNull();
        descriptor.Annotations!.ReadOnlyHint.Should().BeFalse("promotion mutates the catalog");
        descriptor.Annotations.DestructiveHint.Should().BeFalse("promotion creates a layer rather than destroying state");
        descriptor.Annotations.IdempotentHint.Should().BeFalse("service.publish does not honor an idempotency key");

        // Teaches the analyze → publish → render chain and the supported kinds.
        descriptor.Description.Should().Contain("honua_execute_plan");
        descriptor.Description.Should().Contain("honua_query_features");
        descriptor.Description.Should().Contain("FeatureLayer");

        descriptor.OutputSchema.Should().NotBeNull();
        var schema = descriptor.OutputSchema!.Value;
        schema.GetProperty("properties").TryGetProperty("serviceId", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("layerId", out _).Should().BeTrue();

        // Only sourceId is required, mirroring the standard publish_result schema.
        var input = descriptor.InputSchema;
        input.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("sourceId");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenCompleted_PromotesArtifactViaServicePublish_AndReturnsServiceIdAndLayerId()
    {
        OperationRequest? captured = null;
        var invoker = new FakeInvoker((request, _) =>
        {
            captured = request;
            return new OperationHandle
            {
                OperationId = PublishResultTool.PublishOperationId,
                HandleId = "op-abc",
                Status = OperationHandleStatus.Completed,
                MetadataRevision = 42,
                Result = new OperationResultSummary
                {
                    Summary = "Published layer 'Parcels within 500m of flood zone' (id 7) to service 'flood_analysis'.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["layerId"] = "7",
                        ["serviceName"] = "flood_analysis",
                    }
                }
            };
        });

        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument
        {
            SourceId = JobId,
            ArtifactId = ArtifactId,
            ServiceName = "flood_analysis",
        });

        var result = await tool.InvokeAsync(AuthenticatedContext(invoker), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Completed");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        // The layer the agent chains straight into honua_query_features.
        content.GetProperty("serviceId").GetString().Should().Be("flood_analysis");
        content.GetProperty("layerId").GetString().Should().Be("7");
        content.GetProperty("serviceUri").GetString().Should().Be("honua://published-services/flood_analysis");
        content.GetProperty("metadataRevision").GetInt64().Should().Be(42);
        content.GetProperty("sourceJobId").GetString().Should().Be(JobId);
        content.GetProperty("artifactId").GetString().Should().Be(ArtifactId);

        // The promotion reuses the canonical service.publish operation, fed from
        // the artifact's materialized-table coordinates — no parallel path.
        captured.Should().NotBeNull();
        captured!.OperationId.Should().Be("service.publish");
        captured.ConnectionId.Should().Be(ConnectionId);
        captured.ServiceName.Should().Be("flood_analysis");
        captured.Parameters["schema"].Should().Be("honua_data");
        captured.Parameters["table"].Should().Be("imported_parcels_within_500m");
        captured.Parameters["layerName"].Should().Be("Parcels within 500m of flood zone");
        captured.Parameters["srid"].Should().Be("4326");
        captured.Parameters["primaryKey"].Should().Be("id");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenSingleArtifact_SelectsItWithoutArtifactId()
    {
        var invoker = new FakeInvoker((_, _) => new OperationHandle
        {
            OperationId = PublishResultTool.PublishOperationId,
            HandleId = "op-1",
            Status = OperationHandleStatus.Completed,
            Result = new OperationResultSummary
            {
                Summary = "ok",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["layerId"] = "3",
                    ["serviceName"] = "svc",
                }
            }
        });

        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var result = await tool.InvokeAsync(AuthenticatedContext(invoker), arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("layerId").GetString().Should().Be("3");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenRequiresApproval_ReturnsApprovalLane()
    {
        var invoker = new FakeInvoker((_, _) => new OperationHandle
        {
            OperationId = PublishResultTool.PublishOperationId,
            HandleId = "op-pending",
            Status = OperationHandleStatus.RequiresApproval,
            ApprovalLane = "operator-gate",
            Reason = "Publishing requires operator approval on this tier."
        });

        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var result = await tool.InvokeAsync(AuthenticatedContext(invoker), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("RequiresApproval");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("approvalLane").GetString().Should().Be("operator-gate");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenGateDenies_ReturnsDeniedWithReason()
    {
        var invoker = new FakeInvoker((_, _) => new OperationHandle
        {
            OperationId = PublishResultTool.PublishOperationId,
            HandleId = "op-denied",
            Status = OperationHandleStatus.Denied,
            Reason = "The caller is not authorized to publish on this tier."
        });

        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var result = await tool.InvokeAsync(AuthenticatedContext(invoker), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Denied");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("message").GetString().Should().Contain("not authorized");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenJobNotTerminal_SurfacesFailedPrecondition()
    {
        var jobService = JobServiceThrowing(
            new GeoprocessingPreconditionFailedException("Job is not in a terminal state."));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var act = () => tool.InvokeAsync(AuthenticatedContext(new FakeInvoker((_, _) => throw new InvalidOperationException())),
            arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenSelectedArtifactKindUnsupported_SurfacesInvalidArgument()
    {
        var scalar = new ArtifactRef
        {
            ArtifactId = "scalar-1",
            Kind = ArtifactKind.Scalar,
            Label = "Count",
        };
        var jobService = JobServiceReturning(CompletedPackage(scalar));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId, ArtifactId = "scalar-1" });

        var act = () => tool.InvokeAsync(AuthenticatedContext(null), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenArtifactNotMaterializedTable_SurfacesFailedPrecondition()
    {
        var jobService = JobServiceReturning(
            CompletedPackage(FeatureLayerArtifact(withTableCoordinates: false)));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId, ArtifactId = ArtifactId });

        var act = () => tool.InvokeAsync(AuthenticatedContext(null), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenDeploymentTarget_SurfacesFailedPrecondition()
    {
        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument
        {
            SourceId = JobId,
            TargetKind = "deployment",
            RoutePrefix = "/flood-dashboard"
        });

        var act = () => tool.InvokeAsync(AuthenticatedContext(null), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenUnauthenticated_SurfacesAuthorizationError()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var act = () => tool.InvokeAsync(AnonymousContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenInvokerUnavailable_ReturnsFailedWithoutThrowing()
    {
        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));
        var tool = CreateTool(jobService);
        var arguments = Arguments(new McpPublishResultArgument { SourceId = JobId });

        var result = await tool.InvokeAsync(AuthenticatedContext(invoker: null), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Failed");
        content.GetProperty("message").GetString().Should().Contain("operations toolset is unavailable");
    }

    private sealed class FakeInvoker(Func<OperationRequest, OperationPolicyContext, OperationHandle> handler)
        : IOperationInvoker
    {
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request, context));
    }
}
