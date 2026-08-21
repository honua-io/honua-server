// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Server.Features.Operations.Admin;
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

    private static DefaultHttpContext AuthenticatedContext(
        IOperationInvoker? invoker,
        ClaimsPrincipal? principal = null)
    {
        var services = new ServiceCollection();
        if (invoker is not null)
        {
            services.AddSingleton(invoker);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal ?? OidcPrincipal(),
            TraceIdentifier = "publish-result-trace",
        };
        context.Request.Headers["X-Correlation-ID"] = "publish-result-correlation";
        return context;
    }

    private static ClaimsPrincipal OidcPrincipal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, "Publishing Agent"),
        new Claim(ClaimTypes.NameIdentifier, "agent-x"),
        new Claim("iss", "https://issuer.example.com"),
        new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
        new Claim(ClaimTypes.Role, "publisher"),
        new Claim("permission", "services:publish"),
        new Claim("tenant_id", "tenant-a"),
    ], "Oidc"));

    private static ClaimsPrincipal ApiKeyPrincipal(Guid keyId) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, "Publishing API key"),
        new Claim(ClaimTypes.Role, "admin"),
        new Claim("permission", "admin:*"),
        new Claim("auth_type", "admin-api-key"),
        new Claim("api_key_id", keyId.ToString("D")),
        new Claim(
            FrameworkAuthenticationIdentity.CredentialKindClaimType,
            FrameworkAuthenticationIdentity.ApiKeyCredentialKind),
    ], FrameworkAuthenticationIdentity.ApiKeyAuthenticationType));

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
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorResourceType>(),
                Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorOperation>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        jobService.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(package));
        return jobService;
    }

    private static IGeoprocessingJobService JobServiceThrowing(Exception exception)
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorResourceType>(),
                Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorOperation>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
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
        OperationPolicyContext? capturedContext = null;
        var invoker = new FakeInvoker((request, context) =>
        {
            captured = request;
            capturedContext = context;
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
        capturedContext.Should().NotBeNull();
        capturedContext!.PrincipalId.Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer.example.com:agent-x");
        capturedContext.AuthenticationScheme.Should().Be("oidc");
        capturedContext.SubjectId.Should().Be("agent-x");
        capturedContext.SubjectIssuer.Should().Be("https://issuer.example.com");
        capturedContext.Roles.Should().BeEquivalentTo("publisher");
        capturedContext.Permissions.Should().BeEquivalentTo("services:publish");
        capturedContext.TenantId.Should().Be("tenant-a");
        capturedContext.CorrelationId.Should().Be("publish-result-correlation");
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
    public async Task PublishResult_ApiKeyCaller_RealDispatcherAndBridgePersistCanonicalProposal()
    {
        var keyId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        OperationGatewayRequest? captured = null;
        var gateway = Substitute.For<IOperationGateway>();
        gateway.CreateApprovalProposalAsync(
                Arg.Do<OperationGatewayRequest>(request => captured = request),
                Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.ProposalCreated,
                Decision = new GuardrailDecision(
                    GuardrailTier.RequiresApproval,
                    OperationClass.PublishedOperation,
                    default,
                    "test"),
                ProposalId = "proposal-publish-result",
            });
        using var services = new ServiceCollection().AddSingleton(gateway).BuildServiceProvider();
        var invoker = CreateApprovalDispatcher(new AdminOperationApprovalBridge(services));
        var jobService = JobServiceReturning(CompletedPackage(FeatureLayerArtifact()));

        var result = await CreateTool(jobService).InvokeAsync(
            AuthenticatedContext(invoker, ApiKeyPrincipal(keyId)),
            Arguments(new McpPublishResultArgument { SourceId = JobId }),
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        captured.Should().NotBeNull();
        captured!.RequestedBy.Should().Be($"admin-api-key:api-key:{keyId:D}");
        captured.RequestedByAgent.Should().Be(captured.RequestedBy);
        captured.Plan?.ExecutionPayload.Should().Contain(keyId.ToString("D"));
        captured.Plan?.ExecutionPayload.Should().Contain(FrameworkAuthenticationIdentity.ApiKeyCredentialKind);
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

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    public async Task PublishResult_WhenPublishAuthorizationDenied_DoesNotReadOrDispatch()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                Honua.Core.Features.Authorization.Domain.OperatorResourceType.PublishedService,
                Honua.Core.Features.Authorization.Domain.OperatorOperation.Publish,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "publish denied")));
        var invoked = false;
        var invoker = new FakeInvoker((_, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("denied publish must not dispatch");
        });

        var act = () => CreateTool(jobService).InvokeAsync(
            AuthenticatedContext(invoker),
            Arguments(new McpPublishResultArgument { SourceId = JobId }),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        invoked.Should().BeFalse();
        await jobService.DidNotReceive().GetJobResultsAsync(
            Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
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

    private static OperationDispatcher CreateApprovalDispatcher(
        IOperationApprovalProposalBridge approvalBridge)
    {
        var descriptor = new OperationDescriptor
        {
            OperationId = PublishResultTool.PublishOperationId,
            ProviderId = "test",
            Title = "Publish result",
            Description = "Test descriptor.",
            Category = "publishing",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                Determinism = OperationDeterminism.Deterministic,
                SupportsDryRun = false,
                IsIdempotent = false,
            },
        };
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync(PublishResultTool.PublishOperationId, Arg.Any<CancellationToken>())
            .Returns(descriptor);
        var executor = Substitute.For<Honua.Core.Features.Operations.Abstractions.IOperationExecutor>();
        executor.OperationId.Returns(PublishResultTool.PublishOperationId);
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(
                Arg.Any<IOperationDescriptor>(),
                Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "admin-operator",
                Reason = "operator review",
            });

        return new OperationDispatcher(
            catalog,
            [executor],
            policy,
            TimeProvider.System,
            approvalBridge);
    }
}
