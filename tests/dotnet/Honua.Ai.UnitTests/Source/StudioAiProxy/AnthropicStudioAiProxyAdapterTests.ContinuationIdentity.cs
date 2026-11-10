// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Net;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Admission;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

public sealed partial class AnthropicStudioAiProxyAdapterTests
{
    [UnitTest]
    public async Task StreamAsync_DuplicateProviderToolProperties_CompletesWithoutSavingContinuation()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(DuplicateToolPropertiesFixture);
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");

        var events = await CollectAsync(adapter, InitialContinuationRequest(identity));

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop);
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_");
        var continueTurn = () => CollectAsync(adapter, ToolResultContinuationRequest(identity));
        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
    }

    internal static string DuplicateToolPropertiesFixture
        => ToolArgumentsFixture("""{"status":"open","status":"open"}""");

    [UnitTheory]
    [InlineData("1.0", "1")]
    [InlineData("1e0", "1")]
    [InlineData("-0.0", "0")]
    [InlineData("1.2300e2", "123")]
    [InlineData("1e21", "1e+21")]
    [InlineData("1e-7", "0.0000001")]
    [InlineData("9007199254740993.0", "9007199254740993")]
    [InlineData("1e400", "10e399")]
    [InlineData("0.00000000000000000000000000001", "1e-29")]
    public async Task StreamAsync_EquivalentNumericToolArguments_RestoresPrivateContinuation(string providerNumber, string clientNumber)
    {
        var fixture = NumericToolFixture(providerNumber);
        var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextTurnFixture) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        var first = await CollectAsync(adapter, InitialContinuationRequest(identity));
        first.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop);
        var request = NumericContinuation(identity, clientNumber);

        var second = await CollectAsync(adapter, request);

        first.Concat(second).Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.SendCount.Should().Be(2);
        second.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        second.Last().StopReason.Should().Be(StudioAiStopReason.EndTurn);
        using var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var assistant = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        assistant[0].GetProperty("thinking").GetString().Should().Be("PRIVATE_THINKINGPRIVATE_THINKING");
        assistant[0].GetProperty("signature").GetString().Should().Be("PRIVATE_SIGNATURE");
        assistant[1].GetProperty("input").GetProperty("values")[0].GetRawText().Should().Be(providerNumber);
        JsonSerializer.Serialize(first.Concat(second)).Should().NotContain("PRIVATE_");
    }

    [UnitTheory]
    [InlineData("9007199254740992", "9007199254740993")]
    [InlineData("1.00000000000000000000000000001", "1.00000000000000000000000000002")]
    [InlineData("1e400", "2e400")]
    [InlineData("1", "\"1\"")]
    public async Task StreamAsync_DistinctNumericToolArguments_RejectsBeforeHttp(string providerNumber, string clientNumber)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(NumericToolFixture(providerNumber));
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        await CollectAsync(adapter, InitialContinuationRequest(identity));

        var continueTurn = () => CollectAsync(adapter, NumericContinuation(identity, clientNumber));

        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("before-preparation")]
    [InlineData("after-preparation")]
    [InlineData("unchanged")]
    public async Task StreamPreparedAsync_ReferencedCredentialRotation_EnforcesOriginalCredential(string rotation)
    {
        const string reference = "secret://fixture-provider";
        var credential = "fixture-credential-a";
        var secrets = Substitute.For<ISecretProvider>();
        secrets.IsSecretReference(reference).Returns(true);
        secrets.GetSecretOrDefaultAsync(reference, null, Arg.Any<CancellationToken>()).Returns(_ => credential);
        var handler = new StudioAiProxyMockHttpMessageHandler(WithThinking(ToolCallFixture));
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(secrets),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var options = DefaultOptions();
        options.ApiKey = reference;
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        await CollectAsync(adapter, options, InitialContinuationRequest(identity));
        if (rotation == "before-preparation") credential = "fixture-credential-b";
        var invocation = adapter.Prepare(options, ToolResultContinuationRequest(identity));
        secrets.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "GetSecretOrDefaultAsync").Should().Be(1);
        if (rotation == "after-preparation") credential = "fixture-credential-b";
        var events = new List<StudioAiChatEvent>();
        var continueTurn = async () =>
        {
            await foreach (var evt in adapter.StreamPreparedAsync(invocation, RecordingAiAdmission.TransportPermit(), default))
                events.Add(evt);
        };

        if (rotation == "unchanged")
        {
            await continueTurn();
            handler.SendCount.Should().Be(2);
            handler.CapturedRequestBody.Should().Contain("PRIVATE_SIGNATURE");
        }
        else
        {
            var failure = await continueTurn.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.Message.Should().NotContain("fixture-credential").And.NotContain(reference);
            handler.SendCount.Should().Be(1);
            handler.CapturedRequestBody.Should().NotContain("PRIVATE_");
        }
        secrets.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "GetSecretOrDefaultAsync").Should().Be(2);
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_").And.NotContain("fixture-credential").And.NotContain(reference);
    }

    private static string NumericToolFixture(string number)
        => ToolArgumentsFixture("{\"status\":\"open\",\"values\":[" + number + "]}");

    private static string ToolArgumentsFixture(string input)
    {
        var arguments = JsonSerializer.Serialize(input);
        return WithThinking(ToolCallFixture)
            .Replace("\"partial_json\":\"{\\\"status\\\":\"", "\"partial_json\":" + arguments, StringComparison.Ordinal)
            .Replace("\"partial_json\":\"\\\"open\\\"}\"", "\"partial_json\":\"\"", StringComparison.Ordinal);
    }

    private static StudioAiChatRequest NumericContinuation(AiExecutionContext identity, string number)
    {
        var prior = ToolResultContinuationRequest(identity);
        using var arguments = JsonDocument.Parse("{\"values\":[" + number + "],\"status\":\"open\"}");
        return new StudioAiChatRequest
        {
            ExecutionContext = identity,
            Tools = prior.Tools,
            Messages = [prior.Messages[0], new()
            {
                Role = StudioAiRole.Assistant,
                ToolCalls = [new() { Id = "toolu_1", Name = "list_incidents", Arguments = arguments.RootElement.Clone() }]
            }, prior.Messages[2]]
        };
    }
}
