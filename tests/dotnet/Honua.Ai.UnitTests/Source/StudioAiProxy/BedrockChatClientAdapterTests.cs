// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Adapters.Bedrock;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

[Protocol(TestProtocols.TestQuality)]
public sealed class BedrockChatClientAdapterTests
{
    [UnitTest]
    public async Task GetResponseAsync_NestedToolHistory_PreservesJsonValuesInBedrockRequest()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        ConverseRequest? captured = null;
        runtime.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<ConverseRequest>();
                return Task.FromResult(new ConverseResponse
                {
                    Output = new ConverseOutput
                    {
                        Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "Done" }] }
                    }
                });
            });
        using var client = new BedrockChatClientAdapter(runtime, "test-model", ownsRuntime: false);
        var arguments = new Dictionary<string, object?>
        {
            ["draftId"] = "owned-draft",
            ["generation"] = 2L,
            ["layer"] = new Dictionary<string, object?>
            {
                ["id"] = "parcels",
                ["visible"] = true,
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["note"] = null,
                    ["labels"] = new List<object?> { "synthetic", false, 4 }
                },
                ["bounds"] = new object?[] { -157.87, 21.3, null, new Dictionary<string, object?> { ["zoom"] = 12L } }
            }
        };
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Add the parcels layer."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "honua_studio_add_layer", arguments)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"generation\":3}")])
        ];

        var response = await client.GetResponseAsync(messages);

        response.Text.Should().Be("Done");
        captured.Should().NotBeNull();
        var tool = captured!.Messages[1].Content.Single().ToolUse;
        tool.ToolUseId.Should().Be("call-1");
        tool.Name.Should().Be("honua_studio_add_layer");
        var mapped = tool.Input.AsDictionary();
        mapped["draftId"].AsString().Should().Be("owned-draft");
        mapped["generation"].AsLong().Should().Be(2);
        var layer = mapped["layer"].AsDictionary();
        layer["id"].AsString().Should().Be("parcels");
        layer["visible"].AsBool().Should().BeTrue();
        layer["metadata"].AsDictionary()["note"].IsNull().Should().BeTrue();
        var labels = layer["metadata"].AsDictionary()["labels"].AsList();
        labels[0].AsString().Should().Be("synthetic");
        labels[1].AsBool().Should().BeFalse();
        labels[2].AsInt().Should().Be(4);
        var bounds = layer["bounds"].AsList();
        bounds[0].AsDouble().Should().Be(-157.87);
        bounds[1].AsDouble().Should().Be(21.3);
        bounds[2].IsNull().Should().BeTrue();
        bounds[3].AsDictionary()["zoom"].AsLong().Should().Be(12);
        captured.Messages[2].Content.Single().ToolResult.Content.Single().Text.Should().Be("{\"generation\":3}");
    }

    [UnitTest]
    public async Task GetStreamingResponseAsync_NestedToolHistory_ReachesBedrockWithIntactArguments()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        ConverseStreamRequest? captured = null;
        runtime.ConverseStreamAsync(Arg.Any<ConverseStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<ConverseStreamRequest>();
                return Task.FromException<ConverseStreamResponse>(new InvalidOperationException("provider reached"));
            });
        using var client = new BedrockChatClientAdapter(runtime, "test-model", ownsRuntime: false);
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "tool", new Dictionary<string, object?>
            {
                ["view"] = new Dictionary<string, object?> { ["center"] = new object?[] { -157.87, 21.3 } }
            })])
        ];
        var act = async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages))
            {
                // The sentinel provider failure ends this stream before any output.
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider reached");
        captured.Should().NotBeNull();
        var center = captured!.Messages.Single().Content.Single().ToolUse.Input
            .AsDictionary()["view"].AsDictionary()["center"].AsList();
        center.Select(value => value.AsDouble()).Should().Equal(-157.87, 21.3);
    }

    [UnitTest]
    public async Task GetResponseAsync_NonJsonToolArgument_RejectsBeforeCallingBedrock()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        using var client = new BedrockChatClientAdapter(runtime, "test-model", ownsRuntime: false);
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "tool", new Dictionary<string, object?>
            {
                ["nested"] = new object?[] { new object() }
            })])
        ];

        var act = () => client.GetResponseAsync(messages);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*JSON values*");
        await runtime.DidNotReceive().ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>());
    }
}
