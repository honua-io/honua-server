// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies the MCP propose-and-poll tool routes through the operation gateway and
/// returns a structured result with a proposalId + honua://proposals/{id} URI when
/// approval is required (#1696).
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class ProposeOperationToolTests
{
    private static DefaultHttpContext ContextWithGateway(
        IOperationGateway gateway,
        IOperationExecutorCatalog? catalog = null,
        bool multiTenancyEnabled = true,
        string? tenantId = "tenant-1")
    {
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        services.AddSingleton<ITenantContext>(new TestTenantContext(tenantId));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MultiTenancy:Enabled"] = multiTenancyEnabled.ToString(),
            })
            .Build());
        if (catalog != null)
        {
            services.AddSingleton(catalog);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "stable-subject"),
                new Claim(ClaimTypes.Name, "Mutable Agent Display Name"),
            ], "Test"))
        };
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_WhenApprovalRequired_ReturnsProposalIdAndResourceUri()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "test"),
            ProposalId = "proposal-123",
            Message = "Proposal created and awaiting approval."
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy", ResourceId = "target-1", Reason = "ship it" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("proposalId").GetString().Should().Be("proposal-123");
        content.GetProperty("resourceUri").GetString().Should().Be("honua://proposals/proposal-123");
        gateway.LastRequest.Should().NotBeNull();
        gateway.LastRequest!.Authority.Should().NotBeNull();
        gateway.LastRequest.Authority!.Actor.Should().Be("stable-subject");
        gateway.LastRequest.RequestedBy.Should().Be("stable-subject");
        gateway.LastRequest.Authority.EffectiveTenant.Should().Be("tenant-1");
        gateway.LastRequest.Authority.ResourceType.Should().Be(OperatorResourceType.Deployment);
        gateway.LastRequest.Authority.Operation.Should().Be(OperatorOperation.Publish);
        gateway.LastRequest.Authority.ResourceId.Should().Be("target-1");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_UsesProposalOnlyGatewayEvenWhenNormalRouteCouldExecute()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.AdminConfigChange, default, "test"),
            ExecutionOperationId = "exec-1"
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "AdminConfigChange", ResourceId = "config-1" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        gateway.ProposalOnlyCalls.Should().Be(1);
        gateway.RouteCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProposeOperation_Geoprocess_IsRejectedBeforePayloadReachesGateway()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(
                GuardrailTier.DirectExecute,
                OperationClass.Geoprocess,
                default,
                "test"),
        });
        var context = ContextWithGateway(gateway);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "stable-subject"),
            new Claim(OperatorScopeCatalog.ScopeClaimType, OperatorScopeCatalog.ExecuteMutating),
        ], "Bearer"));
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument
            {
                Kind = "Geoprocess",
                ResourceId = "plan-1",
            },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().Should().Be("rejected");
        gateway.LastRequest.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_MultiTenancyDisabled_RoutesWithTenantlessAuthority()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Deploy, default, "test"),
        });
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy", ResourceId = "serving-us-west" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(
            ContextWithGateway(gateway, multiTenancyEnabled: false, tenantId: null),
            arguments,
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        gateway.LastRequest!.Authority!.EffectiveTenant.Should().Be(OperationAuthorityContext.Tenantless);
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_UnknownKind_ReturnsRejected()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Seed, default, "test")
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "NotARealKind" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().Should().Be("rejected");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_ReportsSupportedKindsFromCatalog_OnEveryOutcome()
    {
        // #2563: supportedKinds must reflect genuinely registered executors (MetadataRelease
        // appears, Seed does not) so an agent never hits a silent dead end.
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.NotSupported,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Seed, default, "test"),
            Message = "No executor is registered for operation kind 'Seed'; the operation was not performed."
        });
        var catalog = new FakeExecutorCatalog([OperationClass.AdminConfigChange, OperationClass.Deploy, OperationClass.MetadataRelease]);

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Seed", ResourceId = "seed-1" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway, catalog), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("outcome").GetString().Should().Be("NotSupported");
        var supportedKinds = content.GetProperty("supportedKinds").EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        supportedKinds.Should().BeEquivalentTo(["AdminConfigChange", "Deploy", "MetadataRelease"]);
        supportedKinds.Should().NotContain("Seed", "Seed has no registered executor and must never be advertised as supported");
    }

    private sealed class FakeExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds) : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } = supportedKinds;
    }

    private sealed class TestTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source => TenantContextSource.Claim;

        public bool RequireTenantId(out string tenantId, out string? reason)
        {
            tenantId = TenantId ?? string.Empty;
            reason = TenantId is null ? "no tenant claim present" : null;
            return TenantId is not null;
        }
    }

    private sealed class FakeGateway(OperationGatewayResult result) : IOperationGateway
    {
        public OperationGatewayRequest? LastRequest { get; private set; }
        public int RouteCalls { get; private set; }
        public int ProposalOnlyCalls { get; private set; }

        public Task<OperationGatewayResult> RouteAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
        {
            RouteCalls++;
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationGatewayResult> CreateApprovalProposalAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
        {
            ProposalOnlyCalls++;
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationProposal?> ApplyApprovedProposalAsync(string proposalId, string approvedBy, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);

        public Task<OperationProposal?> RejectProposalAsync(string proposalId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);
    }
}
