// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the REST-equivalent authorization and operator-approval boundary for
/// both hand-authored MCP projections of <c>service.publish</c>.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Mcp, TestProtocols.Admin)]
public sealed class ServicePublishMcpAuthorizationTests
{
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";

    public static IEnumerable<object[]> PublishAuthorizationCases()
    {
        foreach (var tool in Enum.GetValues<PublishTool>())
        {
            foreach (var profile in Enum.GetValues<PrincipalProfile>())
            {
                yield return [tool, profile, false];
                yield return [tool, profile, true];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PublishAuthorizationCases))]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    [Endpoint("POST /api/v1/operations/service.publish/submit")]
    public async Task HandAuthoredPublishTools_MatchRestAdmission_AndStopDeniedActuation(
        PublishTool toolKind,
        PrincipalProfile profile,
        bool approvalRequired)
    {
        var principal = CreatePrincipal(profile);
        var authorization = new MatrixAuthorizationService();
        var approvalEvaluator = new MatrixApprovalEvaluator(approvalRequired);
        var gate = new OperatorApprovalGate(
            Substitute.For<IOperatorAuthorizationEvaluator>(),
            approvalEvaluator,
            NullLogger<OperatorApprovalGate>.Instance);
        var publishing = CreatePublishingSpy();
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
            new MetadataV2GraphSnapshot(
                new MetadataV2Graph { Revision = 42 },
                "\"matrix-etag\"",
                DateTimeOffset.UtcNow));
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        var invoker = new ExecutorInvoker(new ServicePublishExecutor(
            publishing,
            resolver,
            graphProvider,
            TimeProvider.System));
        var httpContext = CreateContext(principal, authorization, gate, invoker);
        var jobService = CreateJobService();
        var tool = CreateTool(toolKind, jobService);
        McpToolsCallResult? result = null;

        var restOutcome = await EvaluateRestTwinAsync(httpContext, principal, gate, approvalRequired);
        var expected = profile == PrincipalProfile.AdminWrite
            ? approvalRequired ? AdmissionOutcome.RequiresApproval : AdmissionOutcome.Allowed
            : AdmissionOutcome.Denied;
        restOutcome.Should().Be(expected,
            $"the canonical REST admission must be stable for profile={profile}, approvalRequired={approvalRequired}");

        Func<Task> invoke = async () =>
        {
            result = await tool.InvokeAsync(httpContext, CreateArguments(toolKind), CancellationToken.None);
        };

        if (expected == AdmissionOutcome.Allowed)
        {
            await invoke();
            result.Should().NotBeNull();
            result!.IsError.Should().BeFalse();
            result.StructuredContent!.Value.GetProperty("metadataRevision").GetInt64().Should().Be(42);
            invoker.LastContext.Should().NotBeNull();
            invoker.LastContext!.AuthorizationOutcome.Should().Be("authorized",
                "the operation context must carry the canonical authorization result");
            await publishing.Received(1).PublishLayerAsync(
                Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
            await graphProvider.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
            return;
        }

        if (expected == AdmissionOutcome.RequiresApproval)
        {
            await invoke.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
        }
        else
        {
            await invoke.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        }

        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        await graphProvider.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
        invoker.SubmitCalls.Should().Be(0,
            "denied and approval-required calls must not create an operation envelope or reach actuation");
    }

    [UnitTest]
    public void PublishAuthorizationInventory_CoversExactMatrixAndAllThreeMcpNames()
    {
        PublishAuthorizationCases().Should().HaveCount(24,
            "the required matrix is 2 tools x 6 principal profiles x 2 approval decisions");

        var descriptor = ServicePublishOperation.BuildDescriptor();
        var names = new[]
        {
            PublishServiceTool.ToolName,
            PublishResultTool.ToolName,
            PublishedOperationTool.ProjectName(descriptor.OperationId),
        };

        names.Should().Equal(
            "honua_publish_service",
            "honua_publish_result",
            "honua_op_service_publish");
        PublishServiceTool.PublishOperationId.Should().Be(descriptor.OperationId);
        PublishResultTool.PublishOperationId.Should().Be(descriptor.OperationId);
        ServicePublishMcpAuthorization.SideEffectClass.Should().Be(descriptor.Policy.SideEffectClass);
        ServicePublishMcpAuthorization.ApprovalModel.Should().Be(descriptor.ApprovalModel);
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
    }

    private static async Task<AdmissionOutcome> EvaluateRestTwinAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        OperatorApprovalGate gate,
        bool approvalRequired)
    {
        var authorization = await OperationAdminAuthorization.EvaluateAsync(
            httpContext,
            principal,
            OperationSideEffectClass.CreatesMetadata,
            CancellationToken.None);
        if (!authorization.IsAuthorized)
        {
            return AdmissionOutcome.Denied;
        }

        var approval = gate.CheckApproval(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Catalog,
                Operation = OperatorOperation.Publish,
            });
        approval.IsRequired.Should().Be(approvalRequired);
        return approval.IsRequired ? AdmissionOutcome.RequiresApproval : AdmissionOutcome.Allowed;
    }

    private static DefaultHttpContext CreateContext(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        OperatorApprovalGate gate,
        IOperationInvoker invoker)
    {
        var services = new ServiceCollection()
            .AddSingleton(authorization)
            .AddSingleton(gate)
            .AddSingleton(invoker)
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services,
            User = principal,
        };
    }

    private static ClaimsPrincipal CreatePrincipal(PrincipalProfile profile)
    {
        if (profile == PrincipalProfile.Anonymous)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, profile.ToString()),
            new(ClaimTypes.NameIdentifier, profile.ToString()),
        };

        switch (profile)
        {
            case PrincipalProfile.SourceJobOwnerReadOnly:
                claims.Add(new Claim(OperatorScopeCatalog.ScopeClaimType, OperatorScopeCatalog.Read));
                break;
            case PrincipalProfile.AdminRead:
                claims.Add(new Claim("matrix-admin-policy", "allowed"));
                claims.Add(new Claim("api_key_id", "matrix-admin-read"));
                claims.Add(new Claim("permission", "admin:read"));
                claims.Add(new Claim(ClaimTypes.Role, "admin"));
                break;
            case PrincipalProfile.OAuthPublishScope:
                claims.Add(new Claim(OperatorScopeCatalog.ScopeClaimType, OperatorScopeCatalog.Publish));
                break;
            case PrincipalProfile.AdminWrite:
                claims.Add(new Claim("matrix-admin-policy", "allowed"));
                claims.Add(new Claim("api_key_id", "matrix-admin-write"));
                claims.Add(new Claim("permission", "admin:write"));
                claims.Add(new Claim(ClaimTypes.Role, "admin"));
                break;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Matrix"));
    }

    private static ILayerPublishingService CreatePublishingSpy()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing.PublishLayerAsync(
                Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 7,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default",
            });
        return publishing;
    }

    private static IGeoprocessingJobService CreateJobService()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync(
                Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                "matrix-result",
                new ResultSummary { Title = "matrix" },
                [new ArtifactRef
                {
                    ArtifactId = "matrix-artifact",
                    Kind = ArtifactKind.Table,
                    Label = "Parcels",
                    Uri = "honua://analysis/artifacts/matrix-artifact",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = ConnectionId,
                        ["schema"] = "public",
                        ["table"] = "parcels",
                    },
                }],
                [],
                new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }));
        return jobService;
    }

    private static IMcpTool CreateTool(PublishTool tool, IGeoprocessingJobService jobService) => tool switch
    {
        PublishTool.PublishService => new PublishServiceTool(NullLogger<PublishServiceTool>.Instance),
        PublishTool.PublishResult => new PublishResultTool(jobService, NullLogger<PublishResultTool>.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    private static System.Text.Json.JsonElement CreateArguments(PublishTool tool) => tool switch
    {
        PublishTool.PublishService => McpTestFactory.ToArguments(
            new McpPublishServiceArgument
            {
                ConnectionId = ConnectionId,
                Schema = "public",
                Table = "parcels",
                LayerName = "Parcels",
            },
            McpJsonContext.Default.McpPublishServiceArgument),
        PublishTool.PublishResult => McpTestFactory.ToArguments(
            new McpPublishResultArgument
            {
                SourceId = "matrix-job",
                ArtifactId = "matrix-artifact",
            },
            McpJsonContext.Default.McpPublishResultArgument),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    private sealed class MatrixAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(Result(user));

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
            => Task.FromResult(Result(user));

        private static AuthorizationResult Result(ClaimsPrincipal user)
            => user.HasClaim("matrix-admin-policy", "allowed")
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed();
    }

    private sealed class MatrixApprovalEvaluator(bool required) : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => required
                ? ApprovalRequirement.Required("operator.publish", "publish-requires-approval")
                : ApprovalRequirement.NotRequired();
    }

    private sealed class ExecutorInvoker(ServicePublishExecutor executor) : IOperationInvoker
    {
        private int _submitCalls;

        public int SubmitCalls => Volatile.Read(ref _submitCalls);

        public OperationPolicyContext? LastContext { get; private set; }

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => executor.ValidateAsync(request, cancellationToken);

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _submitCalls);
            LastContext = context;
            return executor.SubmitAsync(request, context, cancellationToken);
        }
    }

    public enum PublishTool
    {
        PublishService,
        PublishResult,
    }

    public enum PrincipalProfile
    {
        Anonymous,
        AuthenticatedNoGrant,
        SourceJobOwnerReadOnly,
        AdminRead,
        OAuthPublishScope,
        AdminWrite,
    }

    private enum AdmissionOutcome
    {
        Denied,
        RequiresApproval,
        Allowed,
    }
}
