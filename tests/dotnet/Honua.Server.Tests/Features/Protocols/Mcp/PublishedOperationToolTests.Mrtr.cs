// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// In-client approval wraps the agent proposal-only path without changing its decision.
public sealed partial class PublishedOperationToolTests
{
    private const string MrtrOperationId = "admin.services.access-policy.set";
    private const string MrtrArguments = """{"serviceName":"roads","allowAnonymous":true}""";

    [UnitTest]
    public async Task Mrtr_UrlCapableAgentProposal_ReturnsConsoleApprovalAndStillOnlyFilesTheProposal()
    {
        using var catalog = MrtrCatalog();
        var (tool, http, bridge, executor, _) = MrtrSetup(catalog, """{"elicitation":{"url":{}}}""", proposal: null, approvalAction: null);

        var result = await tool.InvokeAsync(http, Args(MrtrArguments), default);

        var approve = result.InputRequired!.InputRequests[ApprovalContinuation.InputKey].GetProperty("params");
        approve.GetProperty("mode").GetString().Should().Be("url");
        approve.GetProperty("url").GetString().Should().Be("https://console.example/inbox?proposalId=proposal-6662");
        bridge.Calls.Should().Be(1);
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Mrtr_FormOnlyClient_KeepsRequiresApprovalResult()
    {
        using var catalog = MrtrCatalog();
        var (tool, http, bridge, _, _) = MrtrSetup(catalog, """{"elicitation":{"form":{}}}""", proposal: null, approvalAction: null);

        var result = await tool.InvokeAsync(http, Args(MrtrArguments), default);

        result.InputRequired.Should().BeNull();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        bridge.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task Mrtr_RetryAfterConsoleApproval_ReportsTheRowWithoutProposingOrExecuting()
    {
        using var catalog = MrtrCatalog();
        var (tool, http, bridge, executor, store) = MrtrSetup(
            catalog, """{"elicitation":{"url":{}}}""", OperationProposalStatus.Succeeded, approvalAction: "accept");

        var result = await tool.InvokeAsync(http, Args(MrtrArguments), default);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Completed");
        await store.Received(1).GetAsync("proposal-6662", Arg.Any<CancellationToken>());
        bridge.Calls.Should().Be(0);
        executor.SubmitCount.Should().Be(0);
    }

    private static OperationCatalog MrtrCatalog() =>
        new([new AdminApiOperationDescriptorProvider()], TimeProvider.System);

    private static (PublishedOperationTool Tool, HttpContext Http, CapturingProposalBridge Bridge, RecordingExecutor Executor, IOperationProposalStore Store)
        MrtrSetup(OperationCatalog catalog, string capabilities, OperationProposalStatus? proposal, string? approvalAction)
    {
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == MrtrOperationId);
        var executor = new RecordingExecutor(MrtrOperationId);
        var bridge = new CapturingProposalBridge();
        var dispatcher = new OperationDispatcher(
            catalog, [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-agent", NullLogger.Instance);
        var http = Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin", "agent"]);
        var actor = McpAuthorizationHelper.ResolveActorId(McpAuthorizationHelper.EnsurePrincipal(http));

        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync("proposal-6662", Arg.Any<CancellationToken>()).Returns(proposal is { } status
            ? new OperationProposal
            {
                ProposalId = "proposal-6662",
                OperationId = MrtrOperationId,
                Kind = default,
                Status = status,
                RequestedBy = actor,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            }
            : null);
        var runtime = new McpMrtrRuntime(
            new McpRequestStateCodec("published-operation-mrtr"),
            McpMrtrRuntime.ParseConsoleUrl("https://console.example"));
        http.RequestServices = new MrtrServices(http.RequestServices, runtime, store);
        http.Items[McpModernProtocol.RequestItemKey] = true;

        var parameters = new McpToolsCallParams
        {
            Name = tool.Name,
            Arguments = Args(MrtrArguments),
            Meta = Args($$$"""{"io.modelcontextprotocol/clientCapabilities":{{{capabilities}}}}"""),
        };
        if (approvalAction is not null)
        {
            var binding = new McpRequestStateBinding(McpAuthorizationHelper.ResolveSessionBindingKey(http)!, tool.Name, parameters.Arguments);
            var state = runtime.Codec!.Mint(binding, new(McpRequestStateKind.Approval, "proposal-6662", runtime.Codec.UtcNow.AddMinutes(5)));
            parameters.RequestState = Args(JsonSerializer.Serialize(state));
            parameters.InputResponses = Args($$$"""{"approve":{"action":"{{{approvalAction}}}"}}""");
        }

        McpRequestContinuation.Refusal(http, tool, parameters).Should().BeNull();
        return (tool, http, bridge, executor, store);
    }

    private sealed class MrtrServices(IServiceProvider inner, McpMrtrRuntime runtime, IOperationProposalStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(McpMrtrRuntime) ? runtime
            : serviceType == typeof(IOperationProposalStore) ? store
            : inner.GetService(serviceType);
    }
}
