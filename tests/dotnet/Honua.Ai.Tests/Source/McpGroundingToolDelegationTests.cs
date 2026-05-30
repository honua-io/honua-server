// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies that the grounding MCP tools (<c>honua_ground_candidates</c> and
/// <c>honua_clarify_intent</c>) parse wire arguments into domain requests,
/// delegate to <see cref="IGroundingService"/>, and translate the canonical
/// result into the published MCP output shape. Error-path tests (missing
/// fields, unknown enums) live on the mapper; these pin the full invocation.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpGroundingToolDelegationTests
{
    private readonly IGroundingService _groundingService = Substitute.For<IGroundingService>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_ValidGoal_DelegatesAndReturnsStructuredOutput()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SamplePublishResult());

        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""
            {"goal":"Publish the parcels layer"}
            """);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent.Should().NotBeNull();
        var body = result.StructuredContent!.Value;
        body.GetProperty("engine").GetString().Should().Be("deterministic");
        body.GetProperty("workflowFamily").GetProperty("value").GetString()
            .Should().Be(nameof(WorkflowFamily.PublishData));
        body.GetProperty("draftIntent").GetProperty("intentId").GetString().Should().Be("intent-1");
        body.GetProperty("candidates").GetProperty("datasets").GetArrayLength().Should().Be(1);

        await _groundingService.Received(1).GroundAsync(
            Arg.Is<GroundingRequest>(r => r.Goal == "Publish the parcels layer"),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_EmptyGoal_SurfacesValidationBeforeDelegation()
    {
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"   "}""");

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_NullArguments_SurfacesValidation()
    {
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments: null, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_ForwardsCancellationToService()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SampleAnalyzeResult());
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        using var cts = new CancellationTokenSource();
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Buffer parcels"}""");

        await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, cts.Token);

        await _groundingService.Received(1).GroundAsync(
            Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), cts.Token);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntent_ValidPayload_CarriesResponseIntoDomainAndReturnsOutput()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SampleAnalyzeResult());

        var tool = new ClarifyIntentTool(_groundingService, _jobService, NullLogger<ClarifyIntentTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""
            {
                "intentId":"intent-1",
                "goal":"Buffer the parcels",
                "response":{"answers":{"param.distance":["50"]}}
            }
            """);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent.Should().NotBeNull();

        await _groundingService.Received(1).GroundAsync(
            Arg.Is<GroundingRequest>(r =>
                r.IntentId == "intent-1"
                && r.ClarificationResponse != null
                && r.ClarificationResponse.Answers.ContainsKey("param.distance")),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntent_MissingIntentId_SurfacesValidationBeforeDelegation()
    {
        var tool = new ClarifyIntentTool(_groundingService, _jobService, NullLogger<ClarifyIntentTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""
            {"goal":"Buffer","response":{"answers":{"q1":["a"]}}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntent_MissingAnswers_SurfacesValidationBeforeDelegation()
    {
        var tool = new ClarifyIntentTool(_groundingService, _jobService, NullLogger<ClarifyIntentTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""
            {"intentId":"intent-1","goal":"Buffer"}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_WithClarificationResult_CopiesClarificationEnvelope()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SampleResultWithClarification());

        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Do something vague"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var clarification = result.StructuredContent!.Value.GetProperty("clarification");
        clarification.ValueKind.Should().Be(JsonValueKind.Object);
        clarification.GetProperty("reasonCodes").GetArrayLength().Should().BeGreaterThan(0);
        clarification.GetProperty("questions").GetArrayLength().Should().BeGreaterThan(0);
    }

    private static GroundingResult SamplePublishResult() => new()
    {
        WorkflowFamily = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 0.9
        },
        DraftIntent = new DraftIntent
        {
            IntentId = "intent-1",
            Goal = "Publish parcels",
            WorkflowFamily = WorkflowFamily.PublishData,
            RequestedOutputs = [ArtifactKind.FeatureLayer],
            AssumptionPolicy = AssumptionPolicy.AskWhenMaterial,
            Publishing = PublishIntent.CreateDraft(
                "intent-1",
                PublishSourceKind.FeatureLayer,
                "parcels",
                PublishTargetKind.FeatureService),
            Provenance = new ProvenanceRecord
            {
                Sources = [new ProvenanceSource { SourceId = "parcels", Description = "Parcels" }],
                ProcessDefinitions = []
            }
        },
        Candidates = new CandidateRanking
        {
            Datasets =
            [
                new GroundingCandidate
                {
                    Id = "parcels",
                    Kind = CandidateKind.Dataset,
                    DisplayName = "Parcels",
                    Score = 0.9,
                    ConfidenceBand = ConfidenceBand.High,
                    Evidence = ["name:2"]
                }
            ]
        },
        Engine = "deterministic"
    };

    private static GroundingResult SampleAnalyzeResult() => new()
    {
        WorkflowFamily = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.9
        },
        DraftIntent = new DraftIntent
        {
            IntentId = "intent-1",
            Goal = "Buffer parcels",
            WorkflowFamily = WorkflowFamily.Analyze,
            AssumptionPolicy = AssumptionPolicy.AskWhenMaterial,
            Provenance = new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }
        },
        Candidates = new CandidateRanking(),
        Engine = "deterministic"
    };

    private static GroundingResult SampleResultWithClarification() => new()
    {
        WorkflowFamily = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.4
        },
        DraftIntent = new DraftIntent
        {
            IntentId = "intent-abc",
            Goal = "Do something vague",
            WorkflowFamily = WorkflowFamily.Analyze,
            AssumptionPolicy = AssumptionPolicy.AskWhenMaterial,
            Provenance = new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }
        },
        Candidates = new CandidateRanking(),
        Clarification = new ClarificationRequest
        {
            IntentId = "intent-abc",
            ReasonCodes = [ClarificationReasonCode.LowConfidence],
            Questions =
            [
                new ClarificationQuestion
                {
                    QuestionId = "workflow_family",
                    Kind = ClarificationQuestionKind.SingleSelect,
                    Prompt = "Which workflow?",
                    Options =
                    [
                        new ClarificationOption { Id = "Analyze", Label = "Analyze" },
                        new ClarificationOption { Id = "PublishData", Label = "Publish" }
                    ]
                }
            ]
        },
        Engine = "deterministic"
    };
}
