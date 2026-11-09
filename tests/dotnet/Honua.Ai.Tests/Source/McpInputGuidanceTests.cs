// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpInputGuidanceTests
{
    [UnitTheory]
    [InlineData("permission_denied")]
    [InlineData("approval_required")]
    [InlineData("failed_precondition")]
    public void Enrich_AuthorityRefusals_PreservesPayloadAndSkipsSchema(string code)
    {
        var tool = Substitute.For<IMcpTool>();
        var result = Refusal(code);
        var original = result.StructuredContent!.Value.GetRawText();
        McpInputGuidance.Enrich(tool, null, result).Should().BeSameAs(result);
        result.StructuredContent.Value.GetRawText().Should().Be(original);
        result.Content.Single().Text.Should().Be(original);
        tool.DidNotReceive().Describe();
    }

    [UnitTest]
    public void Enrich_InvalidType_UsesSchemaGuidanceWithoutEchoingArgumentValues()
    {
        const string canary = "private-argument-value";
        using var arguments = JsonDocument.Parse("""{"jobId":{"password":"private-argument-value"}}""");
        using var schema = JsonDocument.Parse("""
            {"type":"object","required":["jobId"],"properties":{
              "jobId":{"type":"string","description":"Job id from honua_list_jobs."}}}
            """);
        var tool = Substitute.For<IMcpTool>();
        tool.Describe().Returns(new McpToolDescriptor { Name = "owned", InputSchema = schema.RootElement.Clone() });
        var result = McpInputGuidance.Enrich(tool, arguments.RootElement, Refusal("invalid_argument"));
        result.IsError.Should().BeTrue();
        var payload = result.StructuredContent!.Value;
        payload.GetRawText().Should().Contain("$.jobId").And.Contain("honua_list_jobs").And.NotContain(canary);
        using var mirror = JsonDocument.Parse(result.Content.Single().Text!);
        JsonElement.DeepEquals(mirror.RootElement, payload).Should().BeTrue();
    }

    private static McpToolsCallResult Refusal(string code)
    {
        using var document = JsonDocument.Parse("{\"code\":\"" + code + "\",\"message\":\"Refused.\"}");
        return new McpToolsCallResult
        {
            IsError = true,
            StructuredContent = document.RootElement.Clone(),
            Content = [new McpContentBlock { Type = "text", Text = document.RootElement.GetRawText() }]
        };
    }
}
