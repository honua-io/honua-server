// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Grounding;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies the clarification-envelope → MCP-native elicitation mapping
/// (honua-server#2484): the pure envelope-to-elicitation projection, and the
/// grounding tools' capability-detected behavior with graceful fallback to the
/// proprietary envelope when the client did not advertise elicitation.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpClarificationElicitationTests
{
    private readonly IGroundingService _groundingService = Substitute.For<IGroundingService>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    // -------------------------------------------------------------------
    // Pure mapper (ClarificationElicitationMapper.TryMap)
    // -------------------------------------------------------------------

    [UnitTest]
    public void TryMap_SingleSelect_ProducesStringEnumWithNames()
    {
        var request = new ClarificationRequest
        {
            IntentId = "intent-1",
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
                        new ClarificationOption { Id = "PublishData", Label = "Publish data" }
                    ]
                }
            ]
        };

        var elicitation = ClarificationElicitationMapper.TryMap(request);

        elicitation.Should().NotBeNull();
        elicitation!.Message.Should().Contain("intent-1").And.Contain("LowConfidence");
        elicitation.RequestedSchema.Type.Should().Be("object");
        elicitation.RequestedSchema.Required.Should().ContainSingle().Which.Should().Be("workflow_family");

        var property = elicitation.RequestedSchema.Properties["workflow_family"];
        property.Type.Should().Be("string");
        property.Description.Should().Be("Which workflow?");
        property.Enum.Should().Equal("Analyze", "PublishData");
        property.EnumNames.Should().Equal("Analyze", "Publish data");
    }

    [UnitTest]
    public void TryMap_FreeTextAndConfirmation_MapToStringAndBoolean()
    {
        var request = new ClarificationRequest
        {
            IntentId = "intent-2",
            ReasonCodes = [ClarificationReasonCode.HeavyOperationConfirmation],
            Questions =
            [
                new ClarificationQuestion
                {
                    QuestionId = "distance",
                    Kind = ClarificationQuestionKind.FreeText,
                    Prompt = "Buffer distance?"
                },
                new ClarificationQuestion
                {
                    QuestionId = "proceed",
                    Kind = ClarificationQuestionKind.Confirmation,
                    Prompt = "Run the heavy operation?"
                }
            ]
        };

        var elicitation = ClarificationElicitationMapper.TryMap(request);

        elicitation.Should().NotBeNull();
        elicitation!.RequestedSchema.Required.Should().Equal("distance", "proceed");

        var freeText = elicitation.RequestedSchema.Properties["distance"];
        freeText.Type.Should().Be("string");
        freeText.Enum.Should().BeNull();

        var confirmation = elicitation.RequestedSchema.Properties["proceed"];
        confirmation.Type.Should().Be("boolean");
    }

    [UnitTest]
    public void TryMap_MultiSelect_IsNotRepresentable_ReturnsNull()
    {
        // The MCP elicitation subset is flat primitives only — no arrays — so a
        // multi-select question cannot be expressed and the whole envelope must
        // fall back to the proprietary shape.
        var request = new ClarificationRequest
        {
            IntentId = "intent-3",
            ReasonCodes = [ClarificationReasonCode.LowConfidence],
            Questions =
            [
                new ClarificationQuestion
                {
                    QuestionId = "layers",
                    Kind = ClarificationQuestionKind.MultiSelect,
                    Prompt = "Which layers?",
                    Options =
                    [
                        new ClarificationOption { Id = "a", Label = "A" },
                        new ClarificationOption { Id = "b", Label = "B" }
                    ]
                }
            ]
        };

        ClarificationElicitationMapper.TryMap(request).Should().BeNull();
    }

    [UnitTest]
    public void TryMap_BlankOrDuplicateQuestionId_ReturnsNull()
    {
        var blank = new ClarificationRequest
        {
            IntentId = "intent-4",
            ReasonCodes = [],
            Questions =
            [
                new ClarificationQuestion
                {
                    QuestionId = "   ",
                    Kind = ClarificationQuestionKind.FreeText,
                    Prompt = "?"
                }
            ]
        };
        ClarificationElicitationMapper.TryMap(blank).Should().BeNull();

        var duplicate = new ClarificationRequest
        {
            IntentId = "intent-5",
            ReasonCodes = [],
            Questions =
            [
                new ClarificationQuestion { QuestionId = "q", Kind = ClarificationQuestionKind.FreeText, Prompt = "A?" },
                new ClarificationQuestion { QuestionId = "q", Kind = ClarificationQuestionKind.FreeText, Prompt = "B?" }
            ]
        };
        ClarificationElicitationMapper.TryMap(duplicate).Should().BeNull();
    }

    // -------------------------------------------------------------------
    // Tool-level capability detection + fallback
    // -------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_SessionSupportsElicitation_EmitsElicitationAndOmitsEnvelope()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ResultWithSingleSelectClarification());

        var context = ElicitationContext(elicitationSupported: true);
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Do something vague"}""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        // The MCP-native elicitation replaces the proprietary envelope.
        body.TryGetProperty("clarification", out var clarification)
            .Should().BeFalse("the proprietary envelope is omitted once elicitation is emitted");
        clarification.ValueKind.Should().Be(JsonValueKind.Undefined);

        var elicitation = body.GetProperty("elicitation");
        elicitation.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        var schema = elicitation.GetProperty("requestedSchema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").GetProperty("workflow_family").GetProperty("enum")
            .GetArrayLength().Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_SessionWithoutElicitation_KeepsEnvelopeAndOmitsElicitation()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ResultWithSingleSelectClarification());

        var context = ElicitationContext(elicitationSupported: false);
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Do something vague"}""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.TryGetProperty("elicitation", out _).Should().BeFalse(
            "a client that did not advertise elicitation gets the proprietary envelope");
        body.GetProperty("clarification").GetProperty("questions").GetArrayLength()
            .Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_NoSessionHeader_KeepsEnvelope()
    {
        // A stateless request (no Mcp-Session-Id) can never be elicitation-capable,
        // so the proprietary envelope is preserved (graceful fallback).
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ResultWithSingleSelectClarification());

        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Do something vague"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.TryGetProperty("elicitation", out _).Should().BeFalse();
        body.GetProperty("clarification").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_MultiSelectClarification_FallsBackToEnvelopeEvenWhenElicitationSupported()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ResultWithMultiSelectClarification());

        var context = ElicitationContext(elicitationSupported: true);
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Do something vague"}""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.TryGetProperty("elicitation", out _).Should().BeFalse(
            "a multi-select envelope is not representable in the elicitation subset");
        body.GetProperty("clarification").GetProperty("questions").GetArrayLength()
            .Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_NoClarification_EmitsNeitherFieldRegardlessOfCapability()
    {
        _groundingService
            .GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ResultWithoutClarification());

        var context = ElicitationContext(elicitationSupported: true);
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Buffer parcels"}""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.TryGetProperty("elicitation", out _).Should().BeFalse();
        body.TryGetProperty("clarification", out _).Should().BeFalse();
    }

    private static DefaultHttpContext ElicitationContext(bool elicitationSupported)
    {
        var sessions = new McpSessionManager();
        sessions.TryCreateSession("sub:test-user", elicitationSupported, out var sessionId);

        var services = new ServiceCollection()
            .AddSingleton(sessions)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")], "Test"))
        };
        context.Request.Headers[McpSessionManager.SessionHeaderName] = sessionId;
        return context;
    }

    private static GroundingResult ResultWithSingleSelectClarification() =>
        BaseResult() with
        {
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
            }
        };

    private static GroundingResult ResultWithMultiSelectClarification() =>
        BaseResult() with
        {
            Clarification = new ClarificationRequest
            {
                IntentId = "intent-abc",
                ReasonCodes = [ClarificationReasonCode.LowConfidence],
                Questions =
                [
                    new ClarificationQuestion
                    {
                        QuestionId = "layers",
                        Kind = ClarificationQuestionKind.MultiSelect,
                        Prompt = "Which layers?",
                        Options =
                        [
                            new ClarificationOption { Id = "a", Label = "A" },
                            new ClarificationOption { Id = "b", Label = "B" }
                        ]
                    }
                ]
            }
        };

    private static GroundingResult ResultWithoutClarification() => BaseResult();

    private static GroundingResult BaseResult() => new()
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
        Engine = "deterministic"
    };
}
