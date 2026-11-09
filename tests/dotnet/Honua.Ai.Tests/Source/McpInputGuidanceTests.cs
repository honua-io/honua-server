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

    [UnitTheory]
    [InlineData(3, 3)]
    [InlineData(1000, 20)]
    public void Enrich_LargeArray_BoundsGuidanceAndPreservesContentMirror(int maxItems, int expected)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                names = new { type = "array", maxItems, items = new { type = "string", description = "Caller-supplied name." } }
            }
        });
        var tool = Substitute.For<IMcpTool>();
        tool.Describe().Returns(new McpToolDescriptor { Name = "owned", InputSchema = schema });
        var arguments = JsonSerializer.SerializeToElement(new { names = Enumerable.Repeat(0, 1000).ToArray() });

        var result = McpInputGuidance.Enrich(tool, arguments, Refusal("invalid_argument"));

        var payload = result.StructuredContent!.Value;
        payload.GetProperty("violations").GetArrayLength().Should().Be(expected);
        payload.GetRawText().Length.Should().BeLessThan(6000);
        result.Content.Single().Text.Should().Be(payload.GetRawText());
    }

    [UnitTest]
    public void Enrich_MalformedDescriptor_PreservesOriginalRefusal()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","required":"jobId","properties":{"jobId":{"type":"string"}}}""");
        var tool = Substitute.For<IMcpTool>();
        tool.Describe().Returns(new McpToolDescriptor { Name = "owned", InputSchema = schema.RootElement.Clone() });
        var result = Refusal("invalid_argument");
        var original = result.StructuredContent!.Value.GetRawText();

        McpInputGuidance.Enrich(tool, null, result).Should().BeSameAs(result);

        result.StructuredContent.Value.GetRawText().Should().Be(original);
        result.Content.Single().Text.Should().Be(original);
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
