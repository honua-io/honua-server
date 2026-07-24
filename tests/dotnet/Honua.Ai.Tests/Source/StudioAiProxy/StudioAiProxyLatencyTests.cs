// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Enforces the NFR-001 latency-overhead budget documented in
/// <c>docs/guides/run-studio-ai-proxy.md</c>: the proxy's own processing overhead — translating the
/// request plus re-parsing and re-emitting every streamed frame — must stay under 100ms for a
/// realistic steady-state 200-frame streamed turn. No network is involved (a canned in-process SSE
/// body), so this isolates the proxy's own cost from upstream provider latency, which the proxy
/// cannot bound.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyLatencyTests
{
    private const int FrameCount = 200;
    private const long BudgetMs = 100;

    [UnitTest]
    public async Task OpenAiAdapter_ParsingA200FrameStream_StaysUnderTheOverheadBudget()
    {
        var options = new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.OpenAiKind,
            Endpoint = "https://openrouter.ai/api/v1",
            Model = "anthropic/claude-sonnet-4.5",
            ApiKey = "test-key",
            MaxTokens = 4096,
            TimeoutSeconds = 60
        };

        var request = new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "stream a long answer" }]
        };

        // Warm up the JIT/JSON-source-gen paths once, untimed, so the measured run reflects
        // steady-state per-call overhead rather than one-time first-call compilation cost — the
        // budget is about the proxy's marginal cost per call, not process cold-start.
        await RunOnceAsync(BuildFixture(10), options, request);

        var stopwatch = Stopwatch.StartNew();
        var textDeltaCount = await RunOnceAsync(BuildFixture(FrameCount), options, request);
        stopwatch.Stop();

        textDeltaCount.Should().Be(FrameCount, "every fixture frame must round-trip to exactly one TextDelta event");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(
            BudgetMs,
            because: "the proxy's own parse/translate/re-emit overhead must stay under the documented NFR-001 budget");
    }

    private static async Task<int> RunOnceAsync(string fixture, StudioAiProxyProviderOptions options, StudioAiChatRequest request)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(fixture);
        using var factory = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new OpenAiCompatibleStudioAiProxyAdapter(
            factory,
            new StudioAiProxyApiKeyResolver(),
            NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);

        var textDeltaCount = 0;
        await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
        {
            if (evt.Type == StudioAiChatEventType.TextDelta)
            {
                textDeltaCount++;
            }
        }

        return textDeltaCount;
    }

    /// <summary>Builds a canned OpenAI-compatible SSE body with <paramref name="frames"/> text-delta chunks.</summary>
    private static string BuildFixture(int frames)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < frames; i++)
        {
            builder.Append("data: {\"choices\":[{\"delta\":{\"content\":\"token").Append(i).Append("\"}}]}\n\n");
        }

        builder.Append("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        builder.Append("data: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":").Append(frames).Append("}}\n\n");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }
}
