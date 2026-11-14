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
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using static Honua.Server.Tests.Features.Protocols.Mcp.Mrtr.McpMrtrTestKit;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpMrtrApprovalTests
{
    private const string OperationId = "layer.publish";
    private const string UrlCapable = """{"elicitation":{"url":{}}}""";

    private readonly IOperationProposalStore _store = Substitute.For<IOperationProposalStore>();

    [UnitTest]
    public async Task TryRequest_UrlCapableRequestAwaitingApproval_PointsAtConsoleProposal()
    {
        await using var services = Services(Substitute.For<IGroundingService>(), _store, "https://console.example");
        var context = Attach(services, UrlCapable, approvalAction: null);

        var result = ApprovalContinuation.TryRequest(context, Pending());

        var input = result!.InputRequired!.InputRequests[ApprovalContinuation.InputKey].GetProperty("params");
        input.GetProperty("mode").GetString().Should().Be("url");
        input.GetProperty("url").GetString().Should().Be("https://console.example/inbox?proposalId=prop-1");
        input.GetProperty("elicitationId").GetString().Should().Be(ProposalId);
    }

    [UnitTheory]
    [InlineData("""{"elicitation":{"form":{}}}""", "https://console.example", OperationHandleStatus.RequiresApproval)]
    [InlineData(UrlCapable, null, OperationHandleStatus.RequiresApproval)]
    [InlineData(UrlCapable, "https://console.example", OperationHandleStatus.Denied)]
    public async Task TryRequest_WithoutUrlModeConsoleOrPendingProposal_KeepsTodaysResult(
        string capabilities, string? consoleUrl, OperationHandleStatus status)
    {
        await using var services = Services(Substitute.For<IGroundingService>(), _store, consoleUrl);
        var context = Attach(services, capabilities, approvalAction: null);

        ApprovalContinuation.TryRequest(context, Pending() with { Status = status }).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(OperationProposalStatus.Succeeded, "accept", "Completed")]
    [InlineData(OperationProposalStatus.Executing, "accept", "Running")]
    [InlineData(OperationProposalStatus.Rejected, "accept", "Rejected")]
    [InlineData(OperationProposalStatus.RolledBack, "accept", "Failed")]
    [InlineData(OperationProposalStatus.AwaitingApproval, "accept", "RequiresApproval")]
    [InlineData(OperationProposalStatus.Succeeded, "decline", "Completed")]
    public async Task TryResume_Retry_ReportsTheProposalRowAndNeverTrustsTheAnswer(
        OperationProposalStatus stored, string action, string expected)
    {
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>()).Returns(Proposal(stored));
        await using var services = Services(Substitute.For<IGroundingService>(), _store, "https://console.example");
        var context = Attach(services, UrlCapable, action);

        var result = await ApprovalContinuation.TryResumeAsync(context, new(OperationId, "reader"), CancellationToken.None);

        result!.InputRequired.Should().BeNull("a pending proposal is reported, not asked for again");
        var output = result.StructuredContent!.Value;
        output.GetProperty("status").GetString().Should().Be(expected);
        output.GetProperty("proposalId").GetString().Should().Be(ProposalId);
    }

    [UnitTheory]
    [InlineData(OperationHandleStatus.Completed)]
    [InlineData(OperationHandleStatus.Failed)]
    [InlineData(OperationHandleStatus.Cancelled)]
    [InlineData(OperationHandleStatus.Indeterminate)]
    public async Task TryResume_SubmittedProposal_ReportsBoundTerminalExecution(OperationHandleStatus status)
    {
        var proposal = Proposal(OperationProposalStatus.Submitted) with
        {
            Audit = new() { OperationInstanceId = "opinst-1" },
            ExecutionOperationId = "job-1",
        };
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        var operations = Substitute.For<IOperationInstanceStore>();
        var operation = Pending() with
        {
            Status = status,
            JobId = "job-1",
            UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
            Reason = "canonical execution outcome",
        };
        operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(operation);
        await using var services = Services(Substitute.For<IGroundingService>(), _store, "https://console.example");
        var context = Attach(services, UrlCapable, "accept");
        using var stores = new ServiceCollection().AddSingleton(_store).AddSingleton(operations).BuildServiceProvider();
        context.RequestServices = stores;

        var result = await ApprovalContinuation.TryResumeAsync(context, new(OperationId, "reader"), CancellationToken.None);

        var output = result!.StructuredContent!.Value;
        output.GetProperty("status").GetString().Should().Be(status.ToString());
        output.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(operation.UpdatedAt);
        output.GetProperty("message").GetString().Should().Be(operation.Reason);
    }

    [UnitTheory]
    [InlineData("someone-else", OperationId)]
    [InlineData("reader", "access-policy.set")]
    public async Task TryResume_ForeignProposal_IsRefused(string actor, string operationId)
    {
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Succeeded));
        await using var services = Services(Substitute.For<IGroundingService>(), _store, "https://console.example");
        var context = Attach(services, UrlCapable, "accept");

        var act = () => ApprovalContinuation.TryResumeAsync(context, new(operationId, actor), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task TryResume_FirstCall_ReturnsNull()
    {
        await using var services = Services(Substitute.For<IGroundingService>(), _store, "https://console.example");
        var context = Attach(services, UrlCapable, approvalAction: null);

        (await ApprovalContinuation.TryResumeAsync(context, new(OperationId, "reader"), CancellationToken.None))
            .Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    private static DefaultHttpContext Attach(IServiceProvider services, string capabilities, string? approvalAction)
    {
        var context = new DefaultHttpContext { RequestServices = services, User = User() };
        context.Items[McpModernProtocol.RequestItemKey] = true;
        var tool = new ApprovalTool();
        var parameters = new McpToolsCallParams
        {
            Name = tool.Name,
            Meta = Json($$$"""{"io.modelcontextprotocol/clientCapabilities":{{{capabilities}}}}"""),
        };
        if (approvalAction is not null)
        {
            var state = Mint(services, Binding(services, tool.Name, arguments: null),
                new(McpRequestStateKind.Approval, ProposalId, InMinutes(services, 5)));
            parameters.RequestState = Json(JsonSerializer.Serialize(state));
            parameters.InputResponses = Json($$$"""{"approve":{"action":"{{{approvalAction}}}"}}""");
        }

        McpRequestContinuation.Refusal(context, tool, parameters).Should().BeNull();
        return context;
    }

    private static OperationHandle Pending() => new()
    {
        OperationInstanceId = "opinst-1",
        OperationId = OperationId,
        CorrelationId = "corr-1",
        Status = OperationHandleStatus.RequiresApproval,
        ProposalId = ProposalId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static OperationProposal Proposal(OperationProposalStatus status) => new()
    {
        ProposalId = ProposalId,
        OperationId = OperationId,
        Kind = default,
        Status = status,
        RequestedBy = "reader",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class ApprovalTool : IMcpTool, IMcpInputRequiredTool
    {
        public string Name => "honua_publish_layer";

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

        public bool Resumes(McpRequestStateKind kind) => kind == McpRequestStateKind.Approval;

        public McpToolDescriptor Describe() => new() { Name = Name };

        public Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
