// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpMrtrPublishToolTests
{
    private const string ProposalId = "prop-publish-1";
    private const string Agent = "agent-key-1";
    internal const string UrlCapable = """{"elicitation":{"url":{}}}""";

    private readonly IOperationProposalStore _store = Substitute.For<IOperationProposalStore>();
    private readonly IOperationInstanceStore _operations = Substitute.For<IOperationInstanceStore>();
    private readonly IOperationInvoker _invoker = Substitute.For<IOperationInvoker>();

    public static TheoryData<string> Tools => new() { PublishServiceTool.ToolName, PublishResultTool.ToolName };

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task AgentPublish_FromUrlModeClient_PointsAtTheConsoleProposal(string toolName)
    {
        _invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(Pending());
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), (null, UrlCapable));
        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        var input = result.InputRequired!.InputRequests[ApprovalContinuation.InputKey].GetProperty("params");
        input.GetProperty("url").GetString().Should().Be($"https://console.example/inbox?proposalId={ProposalId}");
        input.GetProperty("elicitationId").GetString().Should().Be(ProposalId);
    }

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task AgentPublish_WithoutUrlMode_KeepsTheRequiresApprovalHandle(string toolName)
    {
        _invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(Pending());
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), (null, """{"elicitation":{"form":{}}}"""));
        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.InputRequired.Should().BeNull();
        result.StructuredContent!.Value.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be(ProposalId);
    }

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task ApprovalRetry_ReadsTheProposalBack_AndNeverSubmitsAgain(string toolName)
    {
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Succeeded));
        _operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(Completed());
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), ("accept", UrlCapable));
        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.InputRequired.Should().BeNull();
        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Completed");
        result.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be(ProposalId);
        var serviceField = toolName == PublishResultTool.ToolName ? "serviceId" : "serviceName";
        result.StructuredContent.Value.GetProperty(serviceField).GetString().Should().Be("svc-parcels");
        result.StructuredContent.Value.GetProperty("serviceUri").GetString().Should().NotBeNullOrWhiteSpace();
        if (toolName == PublishResultTool.ToolName)
        {
            result.StructuredContent.Value.GetProperty("sourceJobId").GetString().Should().Be("job-1");
            result.StructuredContent.Value.GetProperty("artifactId").GetString().Should().Be("artifact-1");
        }
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task ApprovalRetry_ForAnotherActorsProposal_IsRefused(string toolName)
    {
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>())
            .Returns(Proposal(OperationProposalStatus.Succeeded) with { RequestedBy = "someone-else" });
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), ("accept", UrlCapable));

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task CompletedProposal_WithoutCanonicalResult_IsNotReportedAsSuccess(string toolName)
    {
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Succeeded));
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), ("accept", UrlCapable));

        var act = () => tool.InvokeAsync(context, arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [MemberData(nameof(Tools))]
    public async Task QueuedRetry_ReturnsPollableStatus(string toolName)
    {
        const string jobId = "publish-job-1";
        const string statusUrl = "/api/v1/admin/connections/parcels/layers/jobs/publish-job-1";
        _store.GetAsync(ProposalId, Arg.Any<CancellationToken>())
            .Returns(Proposal(OperationProposalStatus.Submitted) with { ExecutionOperationId = jobId });
        _operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(Queued(jobId, statusUrl));
        await using var services = Services();
        var (tool, arguments) = Tool(toolName);
        var context = Attach(services, (tool, arguments), ("accept", UrlCapable));
        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.InputRequired.Should().BeNull();
        result.IsError.Should().BeFalse();
        AssertMatchesOutputSchema(tool, result);
        var output = result.StructuredContent!.Value;
        output.GetProperty("status").GetString().Should().Be(nameof(OperationHandleStatus.Running));
        output.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        output.GetProperty("proposalId").GetString().Should().Be(ProposalId);
        output.GetProperty("jobId").GetString().Should().Be(jobId);
        output.GetProperty("details").GetProperty("statusUrl").GetString().Should().Be(statusUrl);
        if (toolName == PublishResultTool.ToolName)
        {
            output.GetProperty("sourceJobId").GetString().Should().Be("job-1");
            output.GetProperty("artifactId").GetString().Should().Be("artifact-1");
        }
        output.GetProperty("handleId").GetString().Should().Be(output.GetProperty("operationInstanceId").GetString()).And.Be("opinst-1");
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    internal static void AssertMatchesOutputSchema(IMcpTool tool, McpToolsCallResult result)
    {
        var schema = JSchema.Parse(tool.Describe().OutputSchema!.Value.GetRawText());
        JObject.Parse(result.StructuredContent!.Value.GetRawText()).IsValid(schema, out IList<string> errors)
            .Should().BeTrue(string.Join("; ", errors));
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddLogging().AddSingleton(Options.Create(new McpOptions()))
        .AddSingleton(_ => new McpSessionManager())
        .AddSingleton(new McpMrtrRuntime(
            new McpRequestStateCodec(McpMrtrTestKit.Secret),
            McpMrtrRuntime.ParseConsoleUrl("https://console.example")))
        .AddSingleton(_store).AddSingleton(_operations).AddSingleton(_invoker)
        .AddSingleton<IAuthorizationService, AllowAll>()
        .BuildServiceProvider();

    private static (IMcpTool Tool, JsonElement Arguments) Tool(string toolName) => toolName switch
    {
        PublishServiceTool.ToolName => (
            new PublishServiceTool(NullLogger<PublishServiceTool>.Instance),
            McpMrtrTestKit.Json("""{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","table":"parcels","layerName":"Parcels"}""")),
        _ => (
            new PublishResultTool(CompletedJobService(), NullLogger<PublishResultTool>.Instance),
            McpMrtrTestKit.Json("""{"sourceId":"job-1","artifactId":"artifact-1"}""")),
    };

    internal static DefaultHttpContext Attach(
        IServiceProvider services,
        (IMcpTool Tool, JsonElement Arguments) call,
        (string? Action, string Capabilities) approval)
    {
        var (tool, arguments) = call;
        var (approvalAction, capabilities) = approval;
        var context = new DefaultHttpContext { RequestServices = services, User = AgentUser() };
        context.Items[McpModernProtocol.RequestItemKey] = true;
        var parameters = new McpToolsCallParams
        {
            Name = tool.Name,
            Arguments = arguments,
            Meta = McpMrtrTestKit.Json($$$"""{"io.modelcontextprotocol/clientCapabilities":{{{capabilities}}}}"""),
        };
        if (approvalAction is not null)
        {
            parameters.RequestState = McpMrtrTestKit.Json(JsonSerializer.Serialize(MintApproval(services, context, call)));
            parameters.InputResponses = McpMrtrTestKit.Json($$$"""{"approve":{"action":"{{{approvalAction}}}"}}""");
        }

        McpRequestContinuation.Refusal(context, tool, parameters).Should().BeNull();
        return context;
    }

    private static string MintApproval(IServiceProvider services, HttpContext context, (IMcpTool Tool, JsonElement Arguments) call)
        => McpMrtrTestKit.Mint(
            services,
            new(McpAuthorizationHelper.ResolveSessionBindingKey(context)!, call.Tool.Name, call.Arguments),
            new(McpRequestStateKind.Approval, ProposalId, McpMrtrTestKit.InMinutes(services, 5)));

    internal static string AgentActorId => McpAuthorizationHelper.ResolveActorId(AgentUser())!;

    private static ClaimsPrincipal AgentUser() => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, Agent),
         new(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant)],
        "Test"));

    private static OperationHandle Pending() => new()
    {
        OperationInstanceId = "opinst-1",
        OperationId = PublishServiceTool.PublishOperationId,
        CorrelationId = "corr-1",
        Status = OperationHandleStatus.RequiresApproval,
        ProposalId = ProposalId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static OperationHandle Queued(string jobId, string statusUrl) => Pending() with
    {
        Status = OperationHandleStatus.Queued,
        JobId = jobId,
        Result = new OperationResultSummary
        {
            Summary = "Queued",
            Details = new Dictionary<string, string>
            {
                ["statusUrl"] = statusUrl,
                [PublishResultAttribution.SourceJobDetailKey] = "job-1",
                [PublishResultAttribution.ArtifactDetailKey] = "artifact-1",
            },
        },
    };

    private static OperationHandle Completed() => Pending() with
    {
        Status = OperationHandleStatus.Completed,
        Result = new OperationResultSummary
        {
            Summary = "Published",
            Details = new Dictionary<string, string>
            {
                ["serviceName"] = "svc-parcels",
                ["serviceLayerIndex"] = "0",
                ["storageLayerId"] = "42",
                [PublishResultAttribution.SourceJobDetailKey] = "job-1",
                [PublishResultAttribution.ArtifactDetailKey] = "artifact-1",
            },
        },
    };

    private static OperationProposal Proposal(OperationProposalStatus status) => new()
    {
        ProposalId = ProposalId,
        OperationId = PublishServiceTool.PublishOperationId,
        Kind = default,
        Status = status,
        RequestedBy = AgentActorId,
        Audit = new() { OperationInstanceId = "opinst-1" },
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static IGeoprocessingJobService CompletedJobService()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                "result-1",
                new ResultSummary { Title = "parcels" },
                [new ArtifactRef
                {
                    ArtifactId = "artifact-1",
                    Kind = ArtifactKind.Table,
                    Label = "Parcels",
                    Uri = "honua://analysis/artifacts/artifact-1",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = "11111111-1111-1111-1111-111111111111",
                        ["schema"] = "public",
                        ["table"] = "parcels",
                    },
                }],
                [],
                new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }));
        return jobService;
    }

    internal sealed class AllowAll : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
            => Task.FromResult(AuthorizationResult.Success());
    }
}
