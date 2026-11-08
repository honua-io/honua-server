// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

public sealed partial class StudioAiProxyServiceTests
{
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

    private static StudioAiProxyService CreateAnthropicSigningService(IHttpClientFactory clients)
        => CreateSigningService(ConfigWithOneAnthropicProvider("claude", true),
            new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
                NullLogger<AnthropicStudioAiProxyAdapter>.Instance));
}
