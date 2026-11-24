// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpMrtrStudioToolTests
{
    private static readonly Guid DraftId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly JsonElement Arguments = McpMrtrTestKit.Json(
        """{"draftId":"11111111-1111-1111-1111-111111111111","generation":1,"packageKey":"map","schemaVersion":"1.0"}""");
    private readonly IOperationProposalStore _proposals = Substitute.For<IOperationProposalStore>();
    private readonly IOperationInstanceStore _operations = Substitute.For<IOperationInstanceStore>();
    private readonly IStudioPackageLifecycleService _lifecycle = Substitute.For<IStudioPackageLifecycleService>();
    private readonly IStudioDraftMutationRuntime _mutations = Substitute.For<IStudioDraftMutationRuntime>();
    private readonly UpdateStudioDraftTool _tool = new(Substitute.For<IGeoprocessingJobService>(), NullLogger<UpdateStudioDraftTool>.Instance);

    [UnitTest]
    public async Task InitialUpdate_OffersConsoleApproval()
    {
        _lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(Draft(1));
        _mutations.UpdateAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<StudioDraftMutationContext>(), Arg.Any<CancellationToken>())
            .Returns(new StudioDraftMutationReceipt<StudioPackageDraft> { Operation = Handle(OperationHandleStatus.RequiresApproval) });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), (null, McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.InputRequired!.InputRequests[ApprovalContinuation.InputKey].GetProperty("params")
            .GetProperty("url").GetString().Should().Be("https://console.example/inbox?proposalId=prop-publish-1");
        await _mutations.Received(1).UpdateAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<StudioDraftMutationContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task InitialUpdate_WithoutUrlMode_ReportsTheProposalRequiringApproval()
    {
        _lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(Draft(1));
        _mutations.UpdateAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<StudioDraftMutationContext>(), Arg.Any<CancellationToken>())
            .Returns(new StudioDraftMutationReceipt<StudioPackageDraft> { Operation = Handle(OperationHandleStatus.RequiresApproval) });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), (null, """{"elicitation":{"form":{}}}"""));

        var act = () => _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        var error = await act.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
        error.Which.ProposalId.Should().Be("prop-publish-1");
        await _mutations.Received(1).UpdateAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<StudioDraftMutationContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CompletedRetry_ReturnsRecordedDraft_WithOriginalGeneration_WithoutAnotherUpdate()
    {
        var completed = Draft(2);
        _lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(completed);
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Succeeded));
        _operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(Handle(OperationHandleStatus.Completed) with
        {
            Result = new OperationResultSummary
            {
                Summary = "Updated draft",
                Details = new Dictionary<string, string> { ["payload"] = JsonSerializer.Serialize(completed, StudioJsonContext.Default.StudioPackageDraft) },
            },
        });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        McpMrtrPublishToolTests.AssertMatchesOutputSchema(_tool, result);
        result.StructuredContent!.Value.GetProperty("draftId").GetGuid().Should().Be(DraftId);
        result.StructuredContent.Value.GetProperty("generation").GetInt64().Should().Be(2);
        await _mutations.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default!, default);
        await _lifecycle.Received(1).GetDraftAsync(DraftId, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task StaleApprovalRetry_ReturnsCurrentGeneration_WithoutAnotherUpdate()
    {
        _lifecycle.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(Draft(3));
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>()).Returns(Proposal(OperationProposalStatus.Failed) with
        {
            Plan = new() { ExecutionPayload = Arguments.GetRawText() },
        });
        _operations.GetAsync("opinst-1", Arg.Any<CancellationToken>()).Returns(Handle(OperationHandleStatus.Failed) with
        {
            Reason = StudioDraftGenerationConflictException.ConflictMessage,
        });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var act = () => _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        var error = await act.Should().ThrowAsync<StudioDraftGenerationConflictException>();
        error.Which.CurrentGeneration.Should().Be(3);
        await _mutations.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default!, default);
    }

    [UnitTheory]
    [InlineData(OperationProposalStatus.Rejected)]
    [InlineData(OperationProposalStatus.Failed)]
    [InlineData(OperationProposalStatus.Cancelled)]
    public async Task UnsuccessfulRetry_ReturnsRecordedReason_WithoutAnotherUpdate(OperationProposalStatus status)
    {
        _proposals.GetAsync("prop-publish-1", Arg.Any<CancellationToken>()).Returns(Proposal(status) with { ResolutionReason = "Recorded decision" });
        await using var services = Services();
        var context = McpMrtrPublishToolTests.Attach(services, (_tool, Arguments), ("accept", McpMrtrPublishToolTests.UrlCapable));
        var result = await _tool.InvokeAsync(context, Arguments, CancellationToken.None);

        result.IsError.Should().BeTrue();
        McpMrtrPublishToolTests.AssertMatchesOutputSchema(_tool, result);
        result.StructuredContent!.Value.GetProperty("message").GetString().Should().Be("Recorded decision");
        await _mutations.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default!, default);
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
        await _mutations.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default!, default);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(Options.Create(new McpOptions()))
            .AddSingleton(_ => new McpSessionManager())
            .AddSingleton(new McpMrtrRuntime(new McpRequestStateCodec(McpMrtrTestKit.Secret), McpMrtrRuntime.ParseConsoleUrl("https://console.example")))
            .AddSingleton(_proposals).AddSingleton(_operations).AddSingleton(_lifecycle).AddSingleton(_mutations);
        McpTestFactory.AddAllowingStudioAuthorization(services);
        return services.BuildServiceProvider();
    }

    private static StudioPackageDraft Draft(long generation) => new()
    {
        DraftId = DraftId, ItemId = Guid.NewGuid(), PackageKey = "map", Family = StudioPackageFamily.Map,
        Envelope = new StudioPackageEnvelope { Family = StudioPackageFamily.Map, SchemaVersion = "1.0" },
        OwnerId = "agent-key-1", Generation = generation, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static OperationHandle Handle(OperationHandleStatus status) => new()
    {
        OperationInstanceId = "opinst-1", OperationId = StudioDraftMutationContext.UpdateOperationId, CorrelationId = "corr-1",
        Status = status, ProposalId = "prop-publish-1", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static OperationProposal Proposal(OperationProposalStatus status) => new()
    {
        ProposalId = "prop-publish-1", OperationId = StudioDraftMutationContext.UpdateOperationId, Kind = default,
        Status = status, RequestedBy = McpMrtrPublishToolTests.AgentActorId, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        Audit = new() { OperationInstanceId = "opinst-1" },
    };
}
