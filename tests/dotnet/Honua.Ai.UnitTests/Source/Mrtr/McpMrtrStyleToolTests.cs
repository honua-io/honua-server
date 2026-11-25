// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpMrtrStyleToolTests
{
    private readonly IOperationProposalStore _proposals = Substitute.For<IOperationProposalStore>();
    private readonly IOperationInstanceStore _operations = Substitute.For<IOperationInstanceStore>();
    private readonly IOperationInvoker _invoker = Substitute.For<IOperationInvoker>();
    private readonly ApplyStylePresetTool _tool = new(Substitute.For<IGeoprocessingJobService>(), NullLogger<ApplyStylePresetTool>.Instance);
    private static readonly System.Text.Json.JsonElement Arguments = McpMrtrTestKit.Json(
        """{"serviceId":"svc-parcels","serviceLayerIndex":0,"styleId":" parcels "}""");

    [UnitTest]
    public async Task InitialCall_OffersConsoleApproval()
    {
        _invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(Handle(OperationHandleStatus.RequiresApproval));
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), (null, McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.InputRequired!.InputRequests[ApprovalContinuation.InputKey].GetProperty("params")
            .GetProperty("url").GetString().Should().Be("https://console.example/inbox?proposalId=prop-publish-1");
    }

    [UnitTest]
    public async Task CompletedRetry_ReturnsRecordedStyleOutput_WithoutReadingLivePreset()
    {
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Succeeded));
        _operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(Handle(OperationHandleStatus.Completed));
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        McpMrtrPublishToolTests.AssertMatchesOutputSchema(_tool, result);
        result.StructuredContent!.Value.GetProperty("applied").GetBoolean().Should().BeTrue();
        result.StructuredContent.Value.GetProperty("styleId").GetString().Should().Be("parcels");
        result.StructuredContent.Value.GetProperty("title").GetString().Should().Be("Parcels");
        result.StructuredContent.Value.GetProperty("styleVersion").GetInt32().Should().Be(3);
        await services.GetRequiredService<IStyleCatalog>().DidNotReceiveWithAnyArgs().GetStyleAsync(default!, default);
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [InlineData(OperationProposalStatus.Rejected)]
    [InlineData(OperationProposalStatus.Failed)]
    [InlineData(OperationProposalStatus.Cancelled)]
    public async Task UnsuccessfulRetry_ReturnsRecordedReason(OperationProposalStatus status)
    {
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>())
            .Returns(Proposal(status) with { ResolutionReason = "Recorded decision" });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.IsError.Should().BeTrue();
        McpMrtrPublishToolTests.AssertMatchesOutputSchema(_tool, result);
        result.StructuredContent!.Value.GetProperty("message").GetString().Should().Be("Recorded decision");
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [InlineData(OperationProposalStatus.AwaitingApproval, OperationHandleStatus.RequiresApproval)]
    [InlineData(OperationProposalStatus.Executing, OperationHandleStatus.Running)]
    [InlineData(OperationProposalStatus.Submitted, OperationHandleStatus.Running)]
    public async Task PendingRetry_ReturnsExistingProposalStatus(OperationProposalStatus status, OperationHandleStatus expectedStatus)
    {
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>()).Returns(Proposal(status));
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.InputRequired.Should().BeNull();
        McpMrtrPublishToolTests.AssertMatchesOutputSchema(_tool, result);
        var output = result.StructuredContent!.Value;
        output.GetProperty("status").GetString().Should().Be(expectedStatus.ToString());
        output.GetProperty("proposalId").GetString().Should().Be("prop-publish-1");
        await _invoker.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    private ServiceProvider Services()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync("parcels", Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = "parcels",
            Title = "Parcels",
            MapLibreStyleJson = "{}",
        });
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        approvals.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        return new ServiceCollection().AddLogging()
            .AddSingleton(Options.Create(new McpOptions()))
            .AddSingleton(_ => new McpSessionManager())
            .AddSingleton(new McpMrtrRuntime(new McpRequestStateCodec(McpMrtrTestKit.Secret), McpMrtrRuntime.ParseConsoleUrl("https://console.example")))
            .AddSingleton(_proposals).AddSingleton(_operations).AddSingleton(_invoker)
            .AddSingleton<IAuthorizationService, McpMrtrPublishToolTests.AllowAll>()
            .AddSingleton(new OperatorApprovalGate(Substitute.For<IOperatorAuthorizationEvaluator>(), approvals, NullLogger<OperatorApprovalGate>.Instance))
            .AddSingleton(Substitute.For<IMetadataV2StyleGraphSync>()).AddSingleton(catalog)
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphBuilder()
                .AddResource("res-parcels", "Parcels").AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: 42)
                .AddService("svc-parcels", "Parcels").AddPublication("pub-parcels", "svc-parcels", "res-parcels", layerIndex: 0, storageBindingId: "bind-parcels").BuildProvider())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().AddOptions<RbacOptions>().Services
            .BuildServiceProvider();
    }

    private static OperationHandle Handle(OperationHandleStatus status) => new()
    {
        OperationInstanceId = "opinst-1",
        OperationId = "style.apply-preset",
        CorrelationId = "corr-1",
        Status = status,
        ProposalId = "prop-publish-1",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Result = new OperationResultSummary
        {
            Summary = "Applied preset",
            Details = new Dictionary<string, string>
            {
                ["styleId"] = "parcels",
                ["styleTitle"] = "Parcels",
                ["styleVersion"] = "3",
                ["serviceId"] = "svc-parcels",
                ["serviceLayerIndex"] = "0",
                ["expectedPublicationId"] = "pub-parcels",
                ["expectedResourceId"] = "res-parcels",
                ["expectedStorageBindingId"] = "bind-parcels",
                ["expectedStorageLayerId"] = "42",
            },
        },
    };

    private static OperationProposal Proposal(OperationProposalStatus status) => new()
    {
        ProposalId = "prop-publish-1",
        OperationId = "style.apply-preset",
        Kind = default,
        Status = status,
        RequestedBy = McpMrtrPublishToolTests.AgentActorId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Audit = new() { OperationInstanceId = "opinst-1" },
    };
}
