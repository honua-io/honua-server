// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Geoprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

internal static class McpMrtrTestKit
{
    internal const string Secret = "unit-test-request-state-secret";
    internal const string ProposalId = "prop-1";

    internal static ServiceProvider Services(
        IGroundingService grounding,
        IOperationProposalStore? store = null,
        string? consoleUrl = null)
    {
        IMcpTool[] tools =
        [
            new GroundCandidatesTool(grounding, Substitute.For<IGeoprocessingJobService>(), NullLogger<GroundCandidatesTool>.Instance),
            new PlainTool(),
        ];
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new McpOptions()))
            .AddSingleton(_ => new McpSessionManager())
            .AddSingleton(new McpDataAccessSurface(tools, [], NullLogger<McpDataAccessSurface>.Instance))
            .AddSingleton(new McpMrtrRuntime(new McpRequestStateCodec(Secret), McpMrtrRuntime.ParseConsoleUrl(consoleUrl)))
            .AddSingleton(store ?? Substitute.For<IOperationProposalStore>())
            .BuildServiceProvider();
    }

    internal static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "Test"));

    internal static McpRequestStateBinding Binding(IServiceProvider services, string toolName, string? arguments)
    {
        var key = McpAuthorizationHelper.ResolveSessionBindingKey(new DefaultHttpContext { RequestServices = services, User = User() })!;
        return new McpRequestStateBinding(key, toolName, arguments is null ? null : Json(arguments));
    }

    internal static string Mint(IServiceProvider services, McpRequestStateBinding binding, McpRequestStateGrant grant) =>
        services.GetRequiredService<McpMrtrRuntime>().Codec!.Mint(binding, grant);

    internal static DateTimeOffset InMinutes(IServiceProvider services, int minutes) =>
        services.GetRequiredService<McpMrtrRuntime>().Codec!.UtcNow.AddMinutes(minutes);

    internal static DefaultHttpContext ModernCall(IServiceProvider services, McpCall call)
    {
        var parameters = new JsonObject
        {
            ["name"] = call.ToolName,
            ["arguments"] = JsonNode.Parse(call.Arguments),
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = JsonNode.Parse(call.Capabilities),
            },
        };
        if (call.RequestState is not null)
        {
            parameters["requestState"] = call.RequestState;
        }

        if (call.InputResponses is not null)
        {
            parameters["inputResponses"] = JsonNode.Parse(call.InputResponses);
        }

        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = parameters };
        var context = new DefaultHttpContext { RequestServices = services, User = User() };
        context.Request.Method = "POST";
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Headers["Mcp-Method"] = "tools/call";
        context.Request.Headers["Mcp-Name"] = call.ToolName;
        context.Request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
        context.Response.Body = new MemoryStream();
        return context;
    }

    internal static async Task<JsonElement> SendAsync(IServiceProvider services, McpCall call)
    {
        var context = ModernCall(services, call);
        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    internal static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static GroundingResult Resolved() => new()
    {
        WorkflowFamily = new WorkflowFamilyClassification { Value = WorkflowFamily.Analyze, Confidence = 0.9 },
        DraftIntent = new DraftIntent
        {
            IntentId = "intent-abc",
            Goal = "vague",
            WorkflowFamily = WorkflowFamily.Analyze,
            AssumptionPolicy = AssumptionPolicy.AskWhenMaterial,
            Provenance = new ProvenanceRecord { Sources = [], ProcessDefinitions = [] },
        },
        Candidates = new CandidateRanking(),
        Engine = "deterministic",
    };

    internal static GroundingResult Asking(string questionId) => Resolved() with
    {
        Clarification = new ClarificationRequest
        {
            IntentId = "intent-abc",
            ReasonCodes = [ClarificationReasonCode.LowConfidence],
            Questions =
            [
                new ClarificationQuestion
                {
                    QuestionId = questionId,
                    Kind = ClarificationQuestionKind.SingleSelect,
                    Prompt = "Which one?",
                    Options =
                    [
                        new ClarificationOption { Id = "Analyze", Label = "Analyze" },
                        new ClarificationOption { Id = "PublishData", Label = "Publish" },
                    ],
                },
            ],
        },
    };

    internal sealed record McpCall(string Arguments, string Capabilities)
    {
        public string ToolName { get; init; } = GroundCandidatesTool.ToolName;

        public string? RequestState { get; init; }

        public string? InputResponses { get; init; }
    }

    internal sealed class PlainTool : IMcpTool
    {
        public const string ToolName = "plain_tool";

        public string Name => ToolName;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

        public McpToolDescriptor Describe() => new() { Name = ToolName };

        public Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused requestState must never reach the tool.");
    }

    internal sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
