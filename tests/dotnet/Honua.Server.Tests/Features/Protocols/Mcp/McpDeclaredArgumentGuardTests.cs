// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// tools/call must refuse an argument name that a closed input schema does not declare.
// Dropping it answers a question the caller did not ask: a misspelled filter returns everything.
[Protocol(TestProtocols.Mcp)]
public sealed class McpDeclaredArgumentGuardTests
{
    private const string ClosedSchema =
        """{"type":"object","properties":{"severity":{"type":"string"}},"additionalProperties":false}""";

    [UnitTheory]
    [InlineData("""{"serverity":"critical"}""", "serverity")]
    [InlineData("""{"severity":"critical","Severity":"low"}""", "Severity")]
    public async Task Call_ClosedSchemaWithUndeclaredArgument_IsRefusedBeforeTheToolRuns(string arguments, string unknown)
    {
        var tool = new StubTool(ClosedSchema);

        var result = await CallAsync(tool, arguments);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var body = result.GetProperty("structuredContent");
        body.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.InvalidArgument);
        body.GetProperty("message").GetString().Should().Contain(unknown).And.Contain(StubTool.ToolName)
            .And.NotContain("critical", "a refusal names arguments and never echoes a value");
        tool.Calls.Should().Be(0);
    }

    [UnitTheory]
    [InlineData(ClosedSchema, """{"severity":"critical"}""")]
    [InlineData(ClosedSchema, "{}")]
    [InlineData("""{"type":"object","properties":{"severity":{"type":"string"}}}""", """{"anything":1}""")]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""", """{"anything":"x"}""")]
    public async Task Call_DeclaredEmptyOrOpenSchemaArguments_RunTheTool(string schema, string arguments)
    {
        var tool = new StubTool(schema);

        var result = await CallAsync(tool, arguments);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        tool.Calls.Should().Be(1);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_ops_findings")]
    public async Task Call_OpsFindingsWithMisspelledFilter_IsRefused()
    {
        var tool = new OpsFindingsTool(NullLogger<OpsFindingsTool>.Instance);

        var result = await CallAsync(tool, """{"serverity":"critical"}""");

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("message").GetString()
            .Should().Contain("serverity").And.Contain(OpsFindingsTool.ToolName);
    }

    [UnitTest]
    public void EnsureDeclared_ClosedSchemaWithoutProperties_RefusesEveryNameAndCapsTheList()
    {
        var tool = new StubTool("""{"type":"object","additionalProperties":false}""");
        var names = Enumerable.Range(0, 12).Select(i => $"\"arg{i:00}\":1").Append($"\"{new string('z', 80)}\":1");
        var arguments = Json("{" + string.Join(",", names) + "}");

        var act = () => McpDeclaredArgumentGuard.EnsureDeclared(tool, arguments);

        var message = act.Should().Throw<GeoprocessingValidationException>().Which.Message;
        message.Should().Contain("arg00, arg01").And.Contain("arg09").And.NotContain("arg10").And.Contain("and 3 more");
    }

    private static async Task<JsonElement> CallAsync(IMcpTool tool, string arguments)
    {
        var surface = new McpDataAccessSurface([tool], [], NullLogger<McpDataAccessSurface>.Instance);
        var context = McpTestFactory.AuthenticatedHttpContext();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = Json("\"g1\""),
            Method = "tools/call",
            Params = Json($$"""{"name":"{{tool.Name}}","arguments":{{arguments}}}"""),
        };

        var response = await surface.DispatchAsync(context, request, CancellationToken.None);

        response!.Error.Should().BeNull();
        return response.Result!.Value;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StubTool(string inputSchema) : IMcpTool
    {
        public const string ToolName = "honua_stub_filter";

        public int Calls { get; private set; }

        public string Name => ToolName;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

        public McpToolDescriptor Describe() =>
            new() { Name = ToolName, InputSchema = Json(inputSchema) };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(McpToolHelpers.SuccessJsonElement(Json("{}")));
        }
    }
}
