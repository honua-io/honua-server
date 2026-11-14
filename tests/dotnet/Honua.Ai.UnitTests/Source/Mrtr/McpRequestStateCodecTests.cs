// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.TestKit.Attributes;
using static Honua.Server.Tests.Features.Protocols.Mcp.Mrtr.McpMrtrTestKit;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpRequestStateCodecTests
{
    private const string Principal = "sub:reader";

    [UnitTest]
    public void Read_StateFromAnotherReplica_AcceptsReorderedArguments()
    {
        var clock = new MutableClock();
        var replicaA = new McpRequestStateCodec(Secret, clock);
        var replicaB = new McpRequestStateCodec(Secret, clock);
        var minted = new McpRequestStateBinding(Principal, "tool", Json("""{"a":1,"b":{"y":2,"x":[1,"z"]}}"""));
        var state = replicaA.Mint(minted, new(McpRequestStateKind.Clarification, "intent-1", clock.GetUtcNow().AddMinutes(15)));

        var read = replicaB.Read(state, minted with { Arguments = Json("""{ "b": {"x":[1,"z"],"y":2}, "a":1 }""") });

        read!.Kind.Should().Be(McpRequestStateKind.Clarification);
        read.Reference.Should().Be("intent-1");
        state.Should().StartWith(McpRequestStateCodec.Prefix);
    }

    [UnitTest]
    public void Read_LargestReference_StaysUnderTheEncodedCap()
    {
        var codec = new McpRequestStateCodec(Secret);
        var binding = new McpRequestStateBinding(Principal, "tool", null);
        var reference = new string('r', McpRequestStateCodec.MaxReferenceBytes);

        var state = codec.Mint(binding, new(McpRequestStateKind.Clarification, reference, codec.UtcNow.AddMinutes(1)));

        state.Length.Should().BeLessThanOrEqualTo(McpRequestStateCodec.MaxEncodedLength);
        codec.Read(state, binding)!.Reference.Should().Be(reference);
    }

    [UnitTest]
    public void Read_TamperedForeignOrExpiredState_IsRefused()
    {
        var clock = new MutableClock();
        var codec = new McpRequestStateCodec(Secret, clock);
        var binding = new McpRequestStateBinding(Principal, "tool", Json("""{"goal":"x"}"""));
        var state = codec.Mint(binding, new(McpRequestStateKind.Approval, "p-1", clock.GetUtcNow().AddMinutes(10)));
        var tampered = state[..^2] + (state[^2] == 'A' ? 'B' : 'A') + state[^1];

        codec.Read(tampered, binding).Should().BeNull();
        codec.Read(state, binding with { PrincipalKey = "sub:other" }).Should().BeNull();
        codec.Read(state, binding with { ToolName = "other_tool" }).Should().BeNull();
        codec.Read(state, binding with { Arguments = Json("""{"goal":"y"}""") }).Should().BeNull();
        codec.Read(state, binding with { Arguments = null }).Should().BeNull();
        new McpRequestStateCodec("another-secret", clock).Read(state, binding).Should().BeNull();
        codec.Read("hr1." + new string('A', 2000), binding).Should().BeNull();
        codec.Read(state, binding).Should().NotBeNull();

        clock.Advance(TimeSpan.FromMinutes(10));
        codec.Read(state, binding).Should().BeNull("the state has expired");
    }

    [UnitTheory]
    [InlineData("""{}""", "")]
    [InlineData("""{"elicitation":{}}""", "form")]
    [InlineData("""{"elicitation":{"form":{}}}""", "form")]
    [InlineData("""{"elicitation":{"url":{}}}""", "url")]
    [InlineData("""{"elicitation":{"form":{},"url":{}}}""", "form,url")]
    public void ElicitationModes_DeclaredCapabilities_AreRead(string capabilities, string expectedModes)
    {
        var meta = Json($$$"""{"io.modelcontextprotocol/clientCapabilities":{{{capabilities}}}}""");
        var expected = expectedModes.Split(',', StringSplitOptions.RemoveEmptyEntries);

        McpElicitationModes.Read(meta).Should().Be(new McpElicitationModes
        {
            Form = expected.Contains("form"),
            Url = expected.Contains("url"),
        });
    }

    [UnitTheory]
    [InlineData("https://console.example", "https://console.example/inbox?proposalId=p%2F1")]
    [InlineData("https://console.example/app", "https://console.example/app/inbox?proposalId=p%2F1")]
    [InlineData("http://localhost:5100/", "http://localhost:5100/inbox?proposalId=p%2F1")]
    [InlineData("http://console.example", null)]
    [InlineData("https://console.example/?next=x", null)]
    [InlineData("https://user@console.example", null)]
    [InlineData("console.example", null)]
    public void ApprovalUrl_ConfiguredConsole_AcceptsOnlyHttpsOrLoopback(string configured, string? expected)
    {
        var runtime = new McpMrtrRuntime(null, McpMrtrRuntime.ParseConsoleUrl(configured));

        runtime.ApprovalUrl("p/1")?.AbsoluteUri.Should().Be(expected);
        if (expected is null)
        {
            runtime.ApprovalUrl("p/1").Should().BeNull();
        }
    }
}
