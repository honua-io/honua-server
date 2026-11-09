// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpConnectionDiscoveryIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public async Task InputRefusals_MissingArguments_NameFieldsAndValueSources()
    {
        await using var fixture = CreateStockFixture();
        await fixture.InitializeAsync();
        using var admin = fixture.CreateAdminClient();
        var tools = await fixture.Services.GetRequiredService<McpDataAccessSurface>().GetAllToolsAsync();
        var refusals = new List<string>();
        var gaps = new List<string>();
        foreach (var tool in tools.Where(tool => tool is not IEvidenceBoundProposalTool))
        {
            var descriptor = McpWorkflowViewDescriptorClassifier.Describe(tool);
            if (!descriptor.InputSchema.TryGetProperty("required", out var required) || required.GetArrayLength() == 0) continue;
            var call = await CallAsync(admin, new ToolInvocation(tool.Name, new { }, "full"));
            var result = call.GetProperty("result");
            if (!IsInputRefusal(result)) continue;
            refusals.Add(tool.Name);
            gaps.AddRange(MissingInputGuidance(result, descriptor));
        }
        refusals.Should().NotBeEmpty();
        gaps.Should().BeEmpty("every missing input must carry its schema's source guidance in both channels");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public async Task InputRefusals_InvalidJobIdType_NamesFieldAndProducer()
    {
        await using var fixture = CreateStockFixture();
        await fixture.InitializeAsync();
        using var admin = fixture.CreateAdminClient();
        var call = await CallAsync(admin, new ToolInvocation("honua_get_job_results", new { jobId = 42 }, "full"));
        var result = call.GetProperty("result");
        IsInputRefusal(result).Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("violations").EnumerateArray()
            .Should().Contain(violation => HasInputGuidance(violation, "jobId", "honua_list_jobs"));
        AssertRefusalMirror(result);
    }

    private static bool IsInputRefusal(JsonElement result) =>
        result.GetProperty("isError").GetBoolean()
        && result.GetProperty("structuredContent").TryGetProperty("code", out var code)
        && code.GetString() == "invalid_argument";

    private static IEnumerable<string> MissingInputGuidance(JsonElement result, McpToolDescriptor descriptor)
    {
        AssertRefusalMirror(result);
        var payload = result.GetProperty("structuredContent");
        var violations = payload.TryGetProperty("violations", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().ToArray() : [];
        foreach (var required in descriptor.InputSchema.GetProperty("required").EnumerateArray())
        {
            var name = required.GetString()!;
            var source = descriptor.InputSchema.GetProperty("properties").GetProperty(name).GetProperty("description").GetString()!;
            if (!violations.Any(violation => HasInputGuidance(violation, name, source))) yield return $"{descriptor.Name}.{name}";
        }
    }

    private static bool HasInputGuidance(JsonElement violation, string name, string source) =>
        violation.TryGetProperty("fieldPath", out var path) && path.GetString() == $"$.{name}"
        && violation.GetProperty("message").GetString()!.Contains(source, StringComparison.Ordinal);

    private static void AssertRefusalMirror(JsonElement result)
    {
        var payload = result.GetProperty("structuredContent");
        result.GetProperty("content").EnumerateArray()
            .Count(block => block.GetProperty("type").GetString() == "text" && IsSameJson(block.GetProperty("text").GetString()!, payload))
            .Should().Be(1);
    }
}
