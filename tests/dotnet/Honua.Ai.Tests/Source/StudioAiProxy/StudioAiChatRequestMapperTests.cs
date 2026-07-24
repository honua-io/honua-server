// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
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
        request.Messages.Should().HaveCount(2);
        request.Messages[0].Role.Should().Be(StudioAiRole.User);
        request.Messages[1].Role.Should().Be(StudioAiRole.Tool, "role parsing is case-insensitive");
        request.Messages[1].ToolCallId.Should().Be("call-1");
        request.Tools.Should().ContainSingle(t => t.Name == "list_incidents");
        request.ToolChoice!.Mode.Should().Be(StudioAiToolChoiceMode.Specific);
        request.ToolChoice.ToolName.Should().Be("list_incidents");
        request.MaxTokens.Should().Be(2048);
        request.Temperature.Should().Be(0.2);
    }
}
