// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Deploy and platform-release creation authority with multi-tenancy enabled (#4842).
/// Every case drives the real MCP tool, reader and REST handler; only the admin policy
/// (which a tenant-scoped admin holds) and the downstream stores/gateway are seams.
/// </summary>
[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class DeployControlPlatformAuthorityTests
{
    private const string TenantAdmin = "tenant-admin";
    private const string ApprovedTenantCredential = "approved-tenant-credential";
    private const string PlatformAdmin = "platform-admin";
    private const string SingleTenant = "single-tenant";
    private const string UnboundAdmin = "unbound-admin";

    private static readonly string[] ProposalTools =
    [
        ProposeFindingTool.ToolName,
        ProposeDeployOperationTool.ToolName,
        ProposeRollbackTool.ToolName,
        ProposePlatformReleaseConvergenceTool.ToolName,
    ];

    private static readonly string[] RestHandlers =
    [
        "HandlePlanDeployOperation",
        "HandleCreateDeployOperation",
        "HandleSubmitDeployOperation",
        "HandlePromoteDeployOperation",
        "HandleRollbackDeployOperation",
        "HandleConvergePlatformRelease",
    ];

    public static TheoryData<string, string> DeniedMcpCases => Cross(
        [.. ProposalTools, ProposeDeployPlanTool.ToolName], [TenantAdmin, ApprovedTenantCredential]);

    public static TheoryData<string, string> AllowedMcpCases => Cross(
        ProposalTools, [PlatformAdmin, SingleTenant, UnboundAdmin]);

    public static TheoryData<string, string> DeniedRestCases => Cross(RestHandlers, [TenantAdmin, ApprovedTenantCredential]);

    public static TheoryData<string, string> AllowedRestCases => Cross(RestHandlers, [PlatformAdmin, SingleTenant, UnboundAdmin]);

    [Theory]
    [MemberData(nameof(DeniedMcpCases))]
    public async Task McpProposal_TenantBoundAdminWithMultiTenancy_IsDeniedWithoutProposalRouteOrAcceptance(
        string toolName, string scenario)
    {
        var harness = McpHarness.Create(scenario);

        var act = () => harness.InvokeAsync(toolName);

        var denial = (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>()).Which;
        denial.ResourceType.Should().Be(OperatorResourceType.Deployment);
        var error = McpErrorMapper.Map(denial);
        error.Data!.Code.Should().Be("permission_denied");
        error.Data.StudioAuthorizationCode.Should().Be("platform_admin_required");
        error.Message.Should().Contain("platform administrator");
        await harness.AssertNothingProposedAsync();
    }

    [Theory]
    [MemberData(nameof(AllowedMcpCases))]
    public async Task McpProposal_PlatformAdminUnboundAdminOrSingleTenant_SealsApprovalProposal(
        string toolName, string scenario)
    {
        var harness = McpHarness.Create(scenario);

        var result = await harness.InvokeAsync(toolName);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        await harness.Gateway.Received().CreateApprovalProposalAsync(
            Arg.Any<string>(), Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
        await harness.Gateway.DidNotReceiveWithAnyArgs().RouteAsync(default!, default);
    }

    [Theory]
    [InlineData(PlatformAdmin)]
    [InlineData(SingleTenant)]
    [InlineData(UnboundAdmin)]
    public async Task McpDeployPlan_PlatformAdminUnboundAdminOrSingleTenant_ReachesTargetLookup(string scenario)
    {
        var harness = McpHarness.Create(scenario);

        var act = () => harness.InvokeAsync(ProposeDeployPlanTool.ToolName);

        // The fixture registry has no targets: reaching NotFound proves authority passed.
        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [Theory]
    [MemberData(nameof(DeniedRestCases))]
    public async Task RestDeployMutation_TenantBoundAdminWithMultiTenancy_Returns403BeforeAnyDeployCall(
        string handler, string scenario)
    {
        var harness = RestHarness.Create(scenario);

        var result = await harness.InvokeAsync(handler);

        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Extensions["code"].Should().Be(PlatformDeployAuthority.DenialCode);
        harness.Registry.ReceivedCalls().Should().BeEmpty();
        harness.Store.ReceivedCalls().Should().BeEmpty();
        harness.Gateway.ReceivedCalls().Should().BeEmpty();
        harness.Invoker.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllowedRestCases))]
    public async Task RestDeployMutation_PlatformAdminUnboundAdminOrSingleTenant_ReachesDeployWorkflow(
        string handler, string scenario)
    {
        var harness = RestHarness.Create(scenario);

        var result = await harness.InvokeAsync(handler);

        ((IStatusCodeHttpResult)result).StatusCode.Should().NotBe(StatusCodes.Status403Forbidden);
        (harness.Registry.ReceivedCalls().Count() + harness.Store.ReceivedCalls().Count() + harness.Gateway.ReceivedCalls().Count())
            .Should().BePositive("an authorized caller must reach the deploy workflow");
    }

    [Theory]
    [InlineData(TenantAdmin, false)]
    [InlineData(ApprovedTenantCredential, false)]
    [InlineData(PlatformAdmin, true)]
    [InlineData(SingleTenant, true)]
    [InlineData(UnboundAdmin, true)]
    public void IsAuthorized_AppliesTheSingleRule(string scenario, bool expected)
        => PlatformDeployAuthority.IsAuthorized(Principal(scenario), TenantOptions(scenario)).Should().Be(expected);

    private static TheoryData<string, string> Cross(IEnumerable<string> first, IEnumerable<string> second)
    {
        var data = new TheoryData<string, string>();
        foreach (var left in first)
        {
            foreach (var right in second)
            {
                data.Add(left, right);
            }
        }

        return data;
    }

    private static ClaimsPrincipal Principal(string scenario)
    {
        if (scenario == ApprovedTenantCredential)
        {
            return new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("api_key_id", "c9d03ead-f8c8-45d8-996d-bf020abcbd10"),
                    new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole),
                    new Claim(AdminApiKeyPermission.ApprovedOperationTenantClaim, "tenant-a"),
                ],
                AuthenticationExtensions.ApiKeyScheme));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "ops-agent"),
            new(ClaimTypes.NameIdentifier, "ops-agent"),
            new(ClaimTypes.Role, "admin"),
        };
        if (scenario == PlatformAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "platform_admin"));
        }

        if (scenario != UnboundAdmin)
        {
            claims.Add(new Claim("tid", "tenant-a"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TenantContextOptions TenantOptions(string scenario) => new() { Enabled = scenario != SingleTenant };

    private static DeployWorkflowService DeployService(IDeployTargetRegistry registry, IWorkflowOperationStore store)
        => new(registry, [store], Array.Empty<IDeployBackend>(), Substitute.For<IOperatorApprovalEvaluator>(),
            NullLogger<DeployWorkflowService>.Instance);

    private static IOperationGateway ProposalGateway()
    {
        var gateway = Substitute.For<IOperationGateway>();
        var proposed = new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"),
            ProposalId = "deployment-proposal",
        };
        gateway.CreateApprovalProposalAsync(Arg.Any<string>(), Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(proposed);
        gateway.RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>()).Returns(proposed);
        return gateway;
    }

    private sealed class McpHarness
    {
        private McpHarness(ClaimsPrincipal principal, IOperationGateway gateway, IOpsFindingsEvidenceSource findings,
            ServiceProvider services)
        {
            Principal = principal;
            Gateway = gateway;
            Findings = findings;
            Services = services;
        }

        public ClaimsPrincipal Principal { get; }

        public IOperationGateway Gateway { get; }

        public IOpsFindingsEvidenceSource Findings { get; }

        public ServiceProvider Services { get; }

        public static McpHarness Create(string scenario)
        {
            var findings = Substitute.For<IOpsFindingsEvidenceSource>();
            findings.EvaluateWithEvidenceAsync(Arg.Any<CancellationToken>())
                .Returns(McpPlatformOpsReaderTests.DeploymentFindingFixture("complete"));
            var gateway = ProposalGateway();
            var services = McpPlatformOpsReaderTests.CreateServices(
                gateway, findings: findings, tenantOptions: TenantOptions(scenario));
            return new McpHarness(Principal(scenario), gateway, findings, services);
        }

        public async Task<McpToolsCallResult> InvokeAsync(string toolName)
        {
            IMcpTool tool = toolName switch
            {
                ProposeFindingTool.ToolName => new ProposeFindingTool(NullLogger<ProposeFindingTool>.Instance),
                ProposeDeployOperationTool.ToolName => new ProposeDeployOperationTool(NullLogger<ProposeDeployOperationTool>.Instance),
                ProposeDeployPlanTool.ToolName => new ProposeDeployPlanTool(NullLogger<ProposeDeployPlanTool>.Instance),
                ProposeRollbackTool.ToolName => new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance),
                ProposePlatformReleaseConvergenceTool.ToolName => new ProposePlatformReleaseConvergenceTool(
                    NullLogger<ProposePlatformReleaseConvergenceTool>.Instance),
                _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
            };
            var arguments = toolName switch
            {
                ProposeFindingTool.ToolName => """{"findingId":"deployment-fixture","candidateId":"serving-us-west"}""",
                ProposeRollbackTool.ToolName => """{"targetId":"serving-us-west","toRevision":"rev-1"}""",
                ProposePlatformReleaseConvergenceTool.ToolName => "{}",
                _ => """{"targetId":"serving-us-west","desiredRevision":"rev-2"}""",
            };
            var reader = McpPlatformOpsReaderTests.CreateReader(
                scopeAuthorizer: new OperatorScopeAuthorizer(), services: Services);
            using var document = JsonDocument.Parse(arguments);
            await using var toolServices = new ServiceCollection()
                .AddSingleton<IMcpPlatformOpsReader>(reader)
                .BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = toolServices, User = Principal };
            return await tool.InvokeAsync(context, document.RootElement.Clone(), CancellationToken.None);
        }

        public async Task AssertNothingProposedAsync()
        {
            await Gateway.DidNotReceiveWithAnyArgs().CreateApprovalProposalAsync(default!, default!, default);
            await Gateway.DidNotReceiveWithAnyArgs().RouteAsync(default!, default);
            await Services.GetRequiredService<IOperationEnvelopeFactory>().DidNotReceiveWithAnyArgs()
                .CreateAcceptedAsync(default!, default!, default);
            await Findings.DidNotReceiveWithAnyArgs().EvaluateWithEvidenceAsync(default);
        }
    }

    private sealed class RestHarness
    {
        public required HttpContext Context { get; init; }

        public required IDeployTargetRegistry Registry { get; init; }

        public required IWorkflowOperationStore Store { get; init; }

        public required IOperationGateway Gateway { get; init; }

        public required IOperationInvoker Invoker { get; init; }

        public static RestHarness Create(string scenario)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton(Options.Create(TenantOptions(scenario)))
                .BuildServiceProvider();
            return new RestHarness
            {
                Context = new DefaultHttpContext { RequestServices = services, User = Principal(scenario) },
                Registry = Substitute.For<IDeployTargetRegistry>(),
                Store = Substitute.For<IWorkflowOperationStore>(),
                Gateway = ProposalGateway(),
                Invoker = Substitute.For<IOperationInvoker>(),
            };
        }

        public async Task<IResult> InvokeAsync(string handler)
        {
            var method = typeof(DeployControlEndpoints).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull($"'{handler}' is a mapped deploy mutation handler");
            var controlPlaneOptions = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
            controlPlaneOptions.CurrentValue.Returns(McpPlatformOpsReaderTests.CreateOptions());
            var service = DeployService(Registry, Store);
            var arguments = method!.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(HttpContext) => Context,
                var type when type == typeof(DeployWorkflowService) => service,
                var type when type == typeof(IOperationGateway) => Gateway,
                var type when type == typeof(IOperationInvoker) => Invoker,
                var type when type == typeof(IOptionsMonitor<ControlPlaneOptions>) => controlPlaneOptions,
                var type when type == typeof(string) => "deploy-a",
                var type when type == typeof(DeployPlanRequest) => new DeployPlanRequest
                { TargetId = "serving-us-west", DesiredRevision = "rev-2" },
                var type when type == typeof(CreateDeployOperationRequest) => new CreateDeployOperationRequest
                { TargetId = "serving-us-west", DesiredRevision = "rev-2" },
                var type => (object?)Activator.CreateInstance(type),
            }).ToArray();
            return await (Task<IResult>)method.Invoke(null, arguments)!;
        }
    }
}
