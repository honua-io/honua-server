// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("admin.services.access-policy.set")]
    [InlineData("admin.layer.publish")]
    public void AgentGatedAdminOperations_AreOperatorGatedAndProposalOnly(string operationId)
    {
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var tool = new PublishedOperationTool(descriptor, "cat-agent", NullLogger.Instance);

        OperatorJourneyApproval.IsDirectExecute(operationId).Should().BeFalse();
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        tool.Describe().Description.Should().Contain("proposal only");
    }

    [UnitTest]
    public void EnsurePrincipal_ReplacedMcpPrincipal_IsMarkedAsAgent()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Path = "/mcp";
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin")], "OperatorBearer"));

        var principal = Honua.Ai.Protocols.Mcp.McpAuthorizationHelper.EnsurePrincipal(http);

        Honua.Infrastructure.Authentication.AgentPrincipal.IsAgent(principal).Should().BeTrue();
    }

    [UnitTest]
    public void ConfigureJourneyOperations_StillExecuteDirectly()
    {
        OperatorJourneyApproval.ForMutation("admin.connections.create", mutating: true)
            .Should().Be(OperationApprovalModel.None);
    }

    [UnitTheory]
    [InlineData("admin.services.access-policy.set")]
    [InlineData("admin.layer.publish")]
    public async Task Invoke_AgentGatedMutation_FilesProposalWithoutExecuting(string operationId)
    {
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new AdminApiOperationDescriptorProvider()], TimeProvider.System), [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-agent", NullLogger.Instance);
        var http = Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin", "agent"]);

        var result = await tool.InvokeAsync(http, Args("""{"serviceName":"roads","allowAnonymous":true}"""), default);

        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("RequiresApproval");
        content.GetProperty("proposalId").GetString().Should().Be("proposal-6662");
        bridge.Calls.Should().Be(1);
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_LayerPublishPreview_UsesNormalPolicySubmission()
    {
        const string operationId = "admin.layer.publish";
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        descriptor.Policy.SupportsDryRun.Should().BeTrue();
        var calls = 0;
        var invoker = new CapturingInvoker((request, context) =>
        {
            calls++;
            request.OperationId.Should().Be(operationId);
            request.DryRun.Should().BeTrue();
            context.Roles.Should().Contain("agent");
            return CompletedHandle(operationId);
        });
        var tool = new PublishedOperationTool(descriptor, "cat-agent", NullLogger.Instance);
        var http = Context(invoker, authorization: ProposalAuthorization(), roles: ["admin", "agent"]);

        var result = await tool.InvokeAsync(http, Args("""{"dryRun":true}"""), default);

        result.IsError.Should().NotBeTrue();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Completed");
        calls.Should().Be(1);
    }
}
