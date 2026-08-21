// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
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
/// Verifies the MCP <c>honua_publish_service</c> tool (#1951): it advertises the
/// correct write annotations and output schema, routes <c>tools/call</c> through
/// the canonical <see cref="IOperationInvoker"/> (operations toolset /
/// service.publish), and projects the resulting <see cref="OperationHandle"/>
/// into a structured Completed / RequiresApproval / unavailable result.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class PublishServiceToolTests
{
    private static DefaultHttpContext ContextWithInvoker(
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
            TraceIdentifier = "publish-service-trace",
        };
        context.Request.Headers["X-Correlation-ID"] = "publish-service-correlation";
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

    private static PublishServiceTool CreateTool(IGeoprocessingJobService? jobService = null)
    {
        if (jobService is null)
        {
            jobService = Substitute.For<IGeoprocessingJobService>();
            jobService.EnsureCallerAuthorizedAsync(
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorResourceType>(),
                    Arg.Any<Honua.Core.Features.Authorization.Domain.OperatorOperation>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        return new PublishServiceTool(jobService, NullLogger<PublishServiceTool>.Instance);
    }

    private static System.Text.Json.JsonElement Arguments(McpPublishServiceArgument argument)
        => McpTestFactory.ToArguments(argument, McpJsonContext.Default.McpPublishServiceArgument);

    [UnitTest]
    public void Describe_AdvertisesWriteAnnotationsAndOutputSchema()
    {
        var descriptor = CreateTool().Describe();

        descriptor.Name.Should().Be("honua_publish_service");
        descriptor.Title.Should().NotBeNullOrWhiteSpace();
        descriptor.Annotations.Should().NotBeNull();
        descriptor.Annotations!.ReadOnlyHint.Should().BeFalse("publishing mutates the catalog");
        descriptor.Annotations.DestructiveHint.Should().BeFalse("publish creates a layer rather than destroying state");
        descriptor.Annotations.IdempotentHint.Should().BeFalse("service.publish does not honor an idempotency key");

        descriptor.OutputSchema.Should().NotBeNull();
        var schema = descriptor.OutputSchema!.Value;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").TryGetProperty("status", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("serviceUri", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("metadataRevision", out _).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenCompleted_RoutesToInvoker_AndReturnsServiceUriAndRevision()
    {
        OperationRequest? captured = null;
        OperationPolicyContext? capturedContext = null;
        var invoker = new FakeInvoker((request, context) =>
        {
            captured = request;
            capturedContext = context;
            return new OperationHandle
            {
                OperationId = PublishServiceTool.PublishOperationId,
                HandleId = "op-abc",
                Status = OperationHandleStatus.Completed,
                MetadataRevision = 42,
                Result = new OperationResultSummary
                {
                    Summary = "Published layer 'Parcels' (id 7) to service 'cadastre'.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["layerId"] = "7",
                        ["serviceName"] = "cadastre",
                    }
                }
            };
        });

        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels",
            ServiceName = "cadastre",
            Srid = 4326,
            Fields = ["objectid", "owner"]
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Completed");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("serviceUri").GetString().Should().Be("honua://published-services/cadastre");
        content.GetProperty("layerId").GetString().Should().Be("7");
        content.GetProperty("metadataRevision").GetInt64().Should().Be(42);

        // The tool adapts onto the canonical service.publish operation rather than
        // reimplementing publishing.
        captured.Should().NotBeNull();
        captured!.OperationId.Should().Be("service.publish");
        captured.ConnectionId.Should().Be("11111111-1111-1111-1111-111111111111");
        captured.ServiceName.Should().Be("cadastre");
        captured.Parameters["schema"].Should().Be("public");
        captured.Parameters["table"].Should().Be("parcels");
        captured.Parameters["layerName"].Should().Be("Parcels");
        captured.Parameters["srid"].Should().Be("4326");
        captured.Fields.Should().BeEquivalentTo("objectid", "owner");
        capturedContext.Should().NotBeNull();
        capturedContext!.PrincipalId.Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer.example.com:agent-x");
        capturedContext.AuthenticationScheme.Should().Be("oidc");
        capturedContext.SubjectId.Should().Be("agent-x");
        capturedContext.SubjectIssuer.Should().Be("https://issuer.example.com");
        capturedContext.Roles.Should().BeEquivalentTo("publisher");
        capturedContext.Permissions.Should().BeEquivalentTo("services:publish");
        capturedContext.TenantId.Should().Be("tenant-a");
        capturedContext.CorrelationId.Should().Be("publish-service-correlation");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenRequiresApproval_ReturnsApprovalLane()
    {
        var invoker = new FakeInvoker((_, _) => new OperationHandle
        {
            OperationId = PublishServiceTool.PublishOperationId,
            HandleId = "op-pending",
            Status = OperationHandleStatus.RequiresApproval,
            ApprovalLane = "operator-gate",
            Reason = "Publishing requires operator approval on this tier."
        });

        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "conn-1",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels"
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("RequiresApproval");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("approvalLane").GetString().Should().Be("operator-gate");
        content.GetProperty("message").GetString().Should().Be("Publishing requires operator approval on this tier.");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_OidcCaller_RealDispatcherAndBridgePersistCanonicalProposal()
    {
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
                ProposalId = "proposal-publish-service",
            });
        using var services = new ServiceCollection().AddSingleton(gateway).BuildServiceProvider();
        var invoker = CreateApprovalDispatcher(new AdminOperationApprovalBridge(services));

        var result = await CreateTool().InvokeAsync(
            ContextWithInvoker(invoker),
            Arguments(new McpPublishServiceArgument
            {
                ConnectionId = "conn-1",
                Schema = "public",
                Table = "parcels",
                LayerName = "Parcels",
            }),
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        captured.Should().NotBeNull();
        captured!.RequestedBy.Should().Be("oidc:subject:https%3A%2F%2Fissuer.example.com:agent-x");
        captured.RequestedByAgent.Should().Be(captured.RequestedBy);
        captured.Plan?.ExecutionPayload.Should().Contain("https://issuer.example.com");
        captured.Plan?.ExecutionPayload.Should().Contain("agent-x");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenInvokerUnavailable_ReturnsFailedWithoutThrowing()
    {
        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "conn-1",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels"
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker: null), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Failed");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("message").GetString().Should().Contain("operations toolset is unavailable");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenIdentityIsNotDurable_DeniesBeforeDispatch()
    {
        var invoked = false;
        var invoker = new FakeInvoker((_, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("unstable identity must not dispatch");
        });
        var unstable = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "display-only")],
            "Test"));

        var result = await CreateTool().InvokeAsync(
            ContextWithInvoker(invoker, unstable),
            Arguments(new McpPublishServiceArgument
            {
                ConnectionId = "conn-1",
                Schema = "public",
                Table = "parcels",
                LayerName = "Parcels",
            }),
            CancellationToken.None);

        invoked.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Denied");
        content.GetProperty("message").GetString().Should().Contain("stable subject or API-key identity");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenPublishAuthorizationDenied_DoesNotDispatch()
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
            ContextWithInvoker(invoker),
            Arguments(new McpPublishServiceArgument
            {
                ConnectionId = "conn-1",
                Schema = "public",
                Table = "parcels",
                LayerName = "Parcels",
            }),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        invoked.Should().BeFalse();
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
            OperationId = PublishServiceTool.PublishOperationId,
            ProviderId = "test",
            Title = "Publish service",
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
        catalog.GetDescriptorAsync(PublishServiceTool.PublishOperationId, Arg.Any<CancellationToken>())
            .Returns(descriptor);
        var executor = Substitute.For<Honua.Core.Features.Operations.Abstractions.IOperationExecutor>();
        executor.OperationId.Returns(PublishServiceTool.PublishOperationId);
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
