// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Tests for <see cref="StudioAiChatRequestMapper"/>: the wire-to-domain translation the endpoint uses before validation.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiChatRequestMapperTests
{
    [UnitTest]
    public void ToDomain_NoMessages_IsRejected()
    {
        var (request, error) = StudioAiChatRequestMapper.ToDomain(new StudioAiChatHttpRequest { Messages = [] });

        request.Should().BeNull();
        error.Should().NotBeNull();
    }

    [UnitTest]
    public void ToDomain_NullMessages_IsRejected()
    {
        var (request, error) = StudioAiChatRequestMapper.ToDomain(
            new StudioAiChatHttpRequest { Messages = null });

        request.Should().BeNull();
        error.Should().Be("At least one message is required.");
    }

    [UnitTest]
    public void ToDomain_NullMessageContent_IsRejected()
    {
        var (request, error) = StudioAiChatRequestMapper.ToDomain(
            new StudioAiChatHttpRequest
            {
                Messages = [new StudioAiChatHttpMessage { Role = "user", Content = null }]
            });

        request.Should().BeNull();
        error.Should().Be("Message content must not be null.");
    }

    [UnitTest]
    public void ToDomain_UnknownRole_IsRejected()
    {
        var (request, error) = StudioAiChatRequestMapper.ToDomain(new StudioAiChatHttpRequest
        {
            Messages = [new StudioAiChatHttpMessage { Role = "narrator", Content = "hi" }]
        });

        request.Should().BeNull();
        error.Should().Contain("narrator");
    }

    [UnitTest]
    public void ToDomain_UnknownToolChoiceMode_IsRejected()
    {
        var (request, error) = StudioAiChatRequestMapper.ToDomain(new StudioAiChatHttpRequest
        {
            Messages = [new StudioAiChatHttpMessage { Role = "user", Content = "hi" }],
            ToolChoice = new StudioAiChatHttpToolChoice { Mode = "sometimes" }
        });

        request.Should().BeNull();
        error.Should().Contain("sometimes");
    }

    [UnitTest]
    public void ToDomain_WellFormedRequest_MapsEveryField()
    {
        var http = new StudioAiChatHttpRequest
        {
            Provider = "claude",
            Model = "claude-opus-4-1",
            System = "Be terse.",
            Messages =
            [
                new StudioAiChatHttpMessage { Role = "user", Content = "list incidents" },
                new StudioAiChatHttpMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls =
                    [
                        new StudioAiChatHttpToolCall
                        {
                            Id = "call-1",
                            Name = "list_incidents",
                            Arguments = JsonDocument.Parse("""{"status":"open"}""").RootElement.Clone()
                        }
                    ]
                },
                new StudioAiChatHttpMessage { Role = "TOOL", Content = "[]", ToolCallId = "call-1", ToolName = "list_incidents" }
            ],
            Tools =
            [
                new StudioAiChatHttpTool
                {
                    Name = "list_incidents",
                    Description = "List open incidents.",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement
                }
            ],
            ToolChoice = new StudioAiChatHttpToolChoice { Mode = "specific", ToolName = "list_incidents" },
            MaxTokens = 2048,
            Temperature = 0.2
        };

        var (request, error) = StudioAiChatRequestMapper.ToDomain(http);

        error.Should().BeNull();
        request.Should().NotBeNull();
        request!.Provider.Should().Be("claude");
        request.Model.Should().Be("claude-opus-4-1");
        request.System.Should().Be("Be terse.");
        request.Messages.Should().HaveCount(3);
        request.Messages[0].Role.Should().Be(StudioAiRole.User);
        request.Messages[1].Role.Should().Be(StudioAiRole.Assistant);
        request.Messages[1].ToolCalls.Should().ContainSingle(call =>
            call.Id == "call-1" && call.Name == "list_incidents");
        request.Messages[2].Role.Should().Be(StudioAiRole.Tool, "role parsing is case-insensitive");
        request.Messages[2].ToolCallId.Should().Be("call-1");
        request.Tools.Should().ContainSingle(t => t.Name == "list_incidents");
        request.ToolChoice!.Mode.Should().Be(StudioAiToolChoiceMode.Specific);
        request.ToolChoice.ToolName.Should().Be("list_incidents");
        request.MaxTokens.Should().Be(2048);
        request.Temperature.Should().Be(0.2);
    }

    [UnitTest]
    public void ToDomain_JsonNullMessagesArray_ReturnsValidationErrorInsteadOfThrowing()
    {
        // honua-server#3010 review: System.Text.Json assigns a JSON `null` straight through to
        // Messages despite its non-nullable C# declaration -- the compiler's null-state analysis is
        // a build-time convention, not something STJ enforces at deserialize time. Round-trip
        // through the real deserializer (rather than forcing a null via `!`) so this test proves
        // the actual runtime shape the endpoint receives, not just a hypothetical one.
        var http = JsonSerializer.Deserialize(
            """{"messages":null}""",
            StudioAiProxyJsonContext.Default.StudioAiChatHttpRequest)!;

        http.Messages.Should().BeNull("this is exactly the System.Text.Json behavior the guard must tolerate");

        var (request, error) = StudioAiChatRequestMapper.ToDomain(http);

        request.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void ToDomain_NullMessageEntry_IsRejectedInsteadOfThrowing()
    {
        var http = JsonSerializer.Deserialize(
            """{"messages":[null]}""",
            StudioAiProxyJsonContext.Default.StudioAiChatHttpRequest)!;

        var (request, error) = StudioAiChatRequestMapper.ToDomain(http);

        request.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
