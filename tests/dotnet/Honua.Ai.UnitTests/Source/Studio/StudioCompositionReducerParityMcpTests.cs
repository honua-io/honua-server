// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// Replays the Studio client's shared reducer cases through the honua_studio_* tools; the fixture is copied verbatim
// from honua-studio test/mcp/fixtures so both sides are held to the same outcomes.
[Protocol(TestProtocols.Mcp)]
public sealed class StudioCompositionReducerParityMcpTests
{
    private static readonly string[] ComparedBodyMembers =
        ["sourceBindings", "styleRefs", "mapSpec", "layers", "view", "widgets", "controls"];

    private static readonly JsonObject Fixture = LoadFixture();

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    public static TheoryData<string> AcceptedCaseNames() => CaseNamesExpecting("body");

    public static TheoryData<string> RefusedCaseNames() => CaseNamesExpecting("error");

    [UnitTheory]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_layer")]
    [Endpoint("POST /mcp tools/call honua_studio_set_view")]
    [Endpoint("POST /mcp tools/call honua_studio_set_layer_style")]
    [MemberData(nameof(AcceptedCaseNames))]
    public async Task ToolCall_ForAnAcceptedReducerCase_ReachesTheReducerBody(string caseName)
    {
        var testCase = FindCase(caseName);
        var draft = await SeedAsync(testCase);

        var body = await draft.CallAndReadBodyAsync(ToolNamed(testCase), ToolArgumentsOf(testCase));

        var expected = testCase["expect"]!["body"]!.AsObject();
        var actual = JsonNode.Parse(body.GetRawText())!.AsObject();
        foreach (var member in ComparedBodyMembers)
        {
            JsonNode.DeepEquals(expected[member], actual[member]).Should().BeTrue(
                $"'{member}' should be {expected[member]?.ToJsonString() ?? "absent"} but was {actual[member]?.ToJsonString() ?? "absent"}");
        }
    }

    [UnitTheory]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_layer")]
    [Endpoint("POST /mcp tools/call honua_studio_set_view")]
    [Endpoint("POST /mcp tools/call honua_studio_set_layer_style")]
    [MemberData(nameof(RefusedCaseNames))]
    public async Task ToolCall_ForARefusedReducerCase_RefusesWithTheReducerMessageAndKeepsTheDraft(string caseName)
    {
        var testCase = FindCase(caseName);
        var draft = await SeedAsync(testCase);
        var before = await draft.ReadDraftAsync();

        var error = await draft.CallExpectingErrorAsync(ToolNamed(testCase), ToolArgumentsOf(testCase));

        var expected = testCase["expect"]!["error"]!.AsObject();
        error.GetProperty("code").GetString().Should().Be(expected["mcpCode"]!.GetValue<string>());
        AssertReducerMessage(error.GetProperty("message").GetString()!, expected);
        var after = await draft.ReadDraftAsync();
        after.GetProperty("generation").GetInt64().Should().Be(before.GetProperty("generation").GetInt64());
        after.GetProperty("envelope").GetRawText().Should().Be(before.GetProperty("envelope").GetRawText());
    }

    private static void AssertReducerMessage(string actual, JsonObject expected)
    {
        if (expected["message"] is { } message)
        {
            actual.Should().Be(message.GetValue<string>());
            return;
        }

        actual.Should().Contain(expected["messageIncludes"]!.GetValue<string>());
    }

    // The server stores map packages, so the seed carries the package identity the client-side fixture leaves out.
    private Task<StudioMcpDraftHarness> SeedAsync(JsonObject testCase)
    {
        var body = Fixture["bodies"]![testCase["body"]!.GetValue<string>()]!.DeepClone().AsObject();
        body["mapPackageId"] = "parity-map";
        body["format"] = "honua_map_package.v1";
        return StudioMcpDraftHarness.SeedMapDraftAsync(_jobService, body.ToJsonString());
    }

    private IMcpTool ToolNamed(JsonObject testCase) => testCase["tool"]!["name"]!.GetValue<string>() switch
    {
        RemoveStudioLayerTool.ToolName => new RemoveStudioLayerTool(_jobService, NullLogger<RemoveStudioLayerTool>.Instance),
        SetStudioViewTool.ToolName => new SetStudioViewTool(_jobService, NullLogger<SetStudioViewTool>.Instance),
        SetStudioLayerStyleTool.ToolName => new SetStudioLayerStyleTool(_jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
        var name => throw new InvalidOperationException($"The reducer cases name an unmapped tool '{name}'."),
    };

    private static string ToolArgumentsOf(JsonObject testCase) => testCase["tool"]!["arguments"]!.ToJsonString();

    private static TheoryData<string> CaseNamesExpecting(string outcome)
    {
        var names = new TheoryData<string>();
        foreach (var testCase in Fixture["cases"]!.AsArray().Where(testCase => testCase!["expect"]![outcome] is not null))
        {
            names.Add(testCase!["name"]!.GetValue<string>());
        }

        return names;
    }

    private static JsonObject FindCase(string caseName) =>
        Fixture["cases"]!.AsArray()
            .Select(testCase => testCase!.AsObject())
            .Single(testCase => testCase["name"]!.GetValue<string>() == caseName);

    private static JsonObject LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Source", "Studio", "Fixtures", "composition-reducer-cases.v1.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
