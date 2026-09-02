// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Adapters.Bedrock;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Credential-custody tests for the provider API-key resolver.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyApiKeyResolverTests
{
    private const string SecretReference = "secret://studio-ai/provider-key";

    [UnitTest]
    public async Task ResolveAsync_ResolvedReference_ReturnsOnlyResolvedCredential()
    {
        var provider = SecretProviderReturning("resolved-credential");
        var resolver = new StudioAiProxyApiKeyResolver(provider);

        var result = await resolver.ResolveAsync("provider", HostedOptions());

        result.Should().Be("resolved-credential");
        result.Should().NotContain(SecretReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_UnresolvedReference_FailsClosedWithoutReferenceDisclosure(string? result)
    {
        var resolver = new StudioAiProxyApiKeyResolver(SecretProviderReturning(result));

        var action = () => resolver.ResolveAsync("provider", HostedOptions());

        var exception = await action.Should().ThrowAsync<StudioAiProxyCredentialUnavailableException>();
        exception.Which.Message.Should().NotContain(SecretReference);
        exception.Which.InnerException.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_SecretProviderFailure_FailsClosedWithoutProviderDetails(bool timeout)
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference(SecretReference).Returns(true);
        provider.GetSecretOrDefaultAsync(SecretReference, null, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(timeout
                ? new TimeoutException($"timeout resolving {SecretReference}")
                : new InvalidOperationException($"failed resolving {SecretReference}")));
        var resolver = new StudioAiProxyApiKeyResolver(provider);

        var action = () => resolver.ResolveAsync("provider", HostedOptions());

        var exception = await action.Should().ThrowAsync<StudioAiProxyCredentialUnavailableException>();
        exception.Which.Message.Should().Be("Provider credentials are unavailable.");
        exception.Which.InnerException.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveAsync_CallerCancellation_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference(SecretReference).Returns(true);
        provider.GetSecretOrDefaultAsync(SecretReference, null, cancellation.Token)
            .Returns<Task<string?>>(_ => throw new OperationCanceledException(cancellation.Token));
        var resolver = new StudioAiProxyApiKeyResolver(provider);

        var action = () => resolver.ResolveAsync("provider", HostedOptions(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    public async Task ResolveAsync_HostedLiteral_FailsClosed()
    {
        var resolver = new StudioAiProxyApiKeyResolver();
        var options = Options("https://provider.example", "literal-key");

        var action = () => resolver.ResolveAsync("provider", options);

        await action.Should().ThrowAsync<StudioAiProxyCredentialUnavailableException>();
    }

    [UnitTest]
    public async Task ResolveAsync_LoopbackLiteral_IsAllowedForLocalDevelopment()
    {
        var resolver = new StudioAiProxyApiKeyResolver();
        var options = Options("https://localhost:11434", "literal-key");

        var result = await resolver.ResolveAsync("provider", options);

        result.Should().Be("literal-key");
    }

    [Theory]
    [InlineData(StudioAiProxyConfiguration.OpenAiKind)]
    [InlineData(StudioAiProxyConfiguration.AnthropicKind)]
    [InlineData(StudioAiProxyConfiguration.BedrockKind)]
    public async Task Adapter_UnresolvedReference_EmitsTypedErrorWithoutUpstreamRequest(string adapterKind)
    {
        var resolver = new StudioAiProxyApiKeyResolver(SecretProviderReturning(null));
        var options = Options("https://provider.example", SecretReference);
        options.Kind = adapterKind;
        var request = new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "sensitive prompt" }]
        };
        List<StudioAiChatEvent> events = [];
        var upstreamCount = 0;

        if (adapterKind == StudioAiProxyConfiguration.BedrockKind)
        {
            var factory = Substitute.For<IBedrockChatClientFactory>();
            var adapter = new BedrockStudioAiProxyAdapter(factory, resolver, NullLogger<BedrockStudioAiProxyAdapter>.Instance);
            await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
            {
                events.Add(evt);
            }

            upstreamCount = factory.ReceivedCalls().Count();
        }
        else
        {
            var handler = new StudioAiProxyMockHttpMessageHandler(string.Empty);
            using var factory = new StudioAiProxyMockHttpClientFactory(handler);
            if (adapterKind == StudioAiProxyConfiguration.OpenAiKind)
            {
                var adapter = new OpenAiCompatibleStudioAiProxyAdapter(factory, resolver, NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);
                await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
                {
                    events.Add(evt);
                }
            }
            else
            {
                var adapter = new AnthropicStudioAiProxyAdapter(factory, resolver, NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
                await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
                {
                    events.Add(evt);
                }
            }

            upstreamCount = handler.SendCount;
        }

        upstreamCount.Should().Be(0);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(StudioAiChatEventType.Error);
        events[0].ErrorCode.Should().Be(StudioAiProxyApiKeyResolver.CredentialUnavailableCode);
        events[0].ErrorMessage.Should().NotContain(SecretReference).And.NotContain("sensitive prompt");
    }

    private static ISecretProvider SecretProviderReturning(string? value)
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference(SecretReference).Returns(true);
        provider.GetSecretOrDefaultAsync(SecretReference, null, Arg.Any<CancellationToken>()).Returns(value);
        return provider;
    }

    private static StudioAiProxyProviderOptions HostedOptions() => Options("https://provider.example", SecretReference);

    private static StudioAiProxyProviderOptions Options(string endpoint, string apiKey) => new()
    {
        Endpoint = endpoint,
        Model = "model",
        ApiKey = apiKey
    };
}
