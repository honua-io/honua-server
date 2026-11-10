// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using System.Text.Json;
using System.Net;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Admission;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

public sealed partial class StudioAiProxyServiceTests
{
    [UnitTest]
    public async Task StreamChatAsync_DuplicateProviderToolProperties_ReleasesSingleSuccessTerminal()
    {
        using var clients = new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(
            AnthropicStudioAiProxyAdapterTests.DuplicateToolPropertiesFixture));
        var service = CreateAnthropicSigningService(clients);
        var request = new StudioAiChatRequest
        {
            ExecutionContext = AiExecutionContext.Create("test", "person-a", "studio-chat"),
            Messages = [new() { Role = StudioAiRole.User, Content = "hi" }]
        };
        var summary = new StudioAiProxyCallSummary();

        var events = await CollectAsync(service, request, summary);

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop);
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        summary.Succeeded.Should().BeTrue();
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_");
    }

    [UnitTest]
    public async Task PrepareChatAsync_AnthropicToolContinuation_UsesVerifiedIdentityAndKeepsReasoningPrivate()
    {
        var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicStudioAiProxyAdapterTests.InterleavedThinkingFixture) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicStudioAiProxyAdapterTests.TextTurnFixture) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var service = CreateAnthropicSigningService(clients);
        var verified = AiExecutionContext.Create("test", "verified-person", "studio-chat");
        var first = new StudioAiChatRequest { Messages = [new() { Role = StudioAiRole.User, Content = "hi" }] };
        List<StudioAiChatEvent> events = [];
        await using (var prepared = await service.PrepareChatAsync(first, verified, CancellationToken.None))
            await foreach (var item in service.StreamPreparedChatAsync(prepared, new StudioAiProxyCallSummary(), CancellationToken.None))
                events.Add(item);
        var call = events.Single(e => e.Type == StudioAiChatEventType.ToolCallStop);
        var next = new StudioAiChatRequest
        {
            ExecutionContext = AiExecutionContext.Create("test", "untrusted-wire-person", "studio-chat"),
            Messages =
            [
                first.Messages[0],
                new() { Role = StudioAiRole.Assistant, Content = "Hello world", ToolCalls =
                    [new() { Id = call.ToolCallId!, Name = "list_incidents", Arguments = call.ToolArguments!.Value }] },
                new() { Role = StudioAiRole.Tool, ToolCallId = call.ToolCallId, Content = "[]" }
            ]
        };

        await using var continuation = await service.PrepareChatAsync(next, verified, CancellationToken.None);
        await foreach (var item in service.StreamPreparedChatAsync(continuation, new StudioAiProxyCallSummary(), CancellationToken.None))
            events.Add(item);

        handler.SendCount.Should().Be(2);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.CapturedRequestBody.Should().Contain("PRIVATE_THINKING").And.Contain("PRIVATE_SIGNATURE");
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_");
    }

    [UnitTheory]
    [MemberData(nameof(AnthropicStudioAiProxyAdapterTests.PostTerminalStreams), MemberType = typeof(AnthropicStudioAiProxyAdapterTests))]
    public async Task StreamChatAsync_AnthropicFrameAfterMessageStop_WithholdsSuccessAndProvenance(string fixture)
    {
        using var clients = new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(fixture));
        var service = CreateAnthropicSigningService(clients);
        var request = CertifiedRequest();
        var summary = new StudioAiProxyCallSummary();
        service.ValidateRequest(request).Should().BeNull();

        var events = await CollectAsync(service, request, summary);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject.ErrorCode.Should()
            .Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop
            || e.Type == StudioAiChatEventType.TranscriptProvenance);
        summary.Succeeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task StreamChatAsync_CompleteAnthropicStream_ReleasesSuccessAndProvenance()
    {
        using var clients = new StudioAiProxyMockHttpClientFactory(
            new StudioAiProxyMockHttpMessageHandler(AnthropicStudioAiProxyAdapterTests.TextTurnFixture));
        var service = CreateAnthropicSigningService(clients);
        var request = CertifiedRequest();
        var summary = new StudioAiProxyCallSummary();
        service.ValidateRequest(request).Should().BeNull();

        var events = await CollectAsync(service, request, summary);

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.TranscriptProvenance);
        summary.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public async Task StreamChatAsync_ThinkingInterleavedWithTextAndTool_ReleasesSuccessAndProvenance()
    {
        using var clients = new StudioAiProxyMockHttpClientFactory(
            new StudioAiProxyMockHttpMessageHandler(AnthropicStudioAiProxyAdapterTests.InterleavedThinkingFixture));
        var service = CreateAnthropicSigningService(clients);
        var summary = new StudioAiProxyCallSummary();

        var events = await CollectAsync(service, CertifiedRequest(), summary);

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.TranscriptProvenance);
        var completed = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop).Subject;
        completed.PromptTokens.Should().Be(12);
        completed.CompletionTokens.Should().Be(8);
        summary.Succeeded.Should().BeTrue();
    }

    private static StudioAiProxyService CreateAnthropicSigningService(IHttpClientFactory clients)
    {
        const string secretReference = "secret://studio-ai/provider-key";
        var configuration = ConfigWithOneAnthropicProvider("claude", true);
        configuration.Providers["claude"].ApiKey = secretReference;
        var secrets = Substitute.For<ISecretProvider>();
        secrets.IsSecretReference(secretReference).Returns(true);
        secrets.GetSecretOrDefaultAsync(secretReference, null, Arg.Any<CancellationToken>()).Returns("test-key");
        return CreateSigningService(configuration,
            new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(secrets),
                NullLogger<AnthropicStudioAiProxyAdapter>.Instance));
    }
}
