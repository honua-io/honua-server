// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// <see cref="StudioAiProxyConfigurationValidator"/> tests: known/unknown adapter kinds, per-kind
/// required fields, and the <c>DefaultProvider</c> cross-reference. Mirrors
/// <c>WorkflowGenerationConfigurationValidator</c>'s per-provider validation style but validates
/// every declared provider (not just the default) because providers here are operator-named, not a
/// fixed compile-time id per adapter.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyConfigurationTests
{
    [UnitTest]
    public void Validate_DisabledFeature_SkipsAllProviderValidation()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = new StudioAiProxyConfiguration { Enabled = false };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_EnabledWithNoProviders_Fails()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = new StudioAiProxyConfiguration { Enabled = true, DefaultProvider = "claude" };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_DefaultProviderNotAmongDeclaredProviders_Fails()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = new StudioAiProxyConfiguration
        {
            Enabled = true,
            DefaultProvider = "missing",
            Providers =
            {
                ["claude"] = new StudioAiProxyProviderOptions
                {
                    Kind = StudioAiProxyConfiguration.AnthropicKind,
                    Endpoint = "https://api.anthropic.com",
                    Model = "claude-sonnet-4-5"
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("missing");
    }

    [UnitTest]
    public void Validate_UnknownAdapterKind_Fails()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = ConfigWith("weird", new StudioAiProxyProviderOptions
        {
            Kind = "not-a-real-kind",
            Endpoint = "https://example.com",
            Model = "some-model"
        });

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("not-a-real-kind");
    }

    [UnitTest]
    public void Validate_AnthropicProvider_RequiresHttps()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = ConfigWith("claude", new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.AnthropicKind,
            Endpoint = "http://api.anthropic.com", // plain HTTP -- rejected for the hosted API shape
            Model = "claude-sonnet-4-5"
        });

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_OpenAiProvider_AllowsPlainHttpLocalEndpoint()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = ConfigWith("local-vllm", new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.OpenAiKind,
            Endpoint = "http://localhost:8000/v1",
            Model = "Qwen2.5-32B-Instruct"
        });

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue(because: "a local OpenAI-compatible endpoint (Ollama/vLLM) need not be HTTPS");
    }

    [UnitTest]
    public void Validate_BedrockProvider_NeedsOnlyAModelId()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = ConfigWith("bedrock-claude", new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.BedrockKind,
            Model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
            Region = "us-west-2"
            // No Endpoint, no ApiKey.
        });

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue(because: "Bedrock uses the AWS credential chain, not an endpoint/key");
    }

    [UnitTest]
    public void Validate_BedrockProvider_WithoutModel_Fails()
    {
        var validator = new StudioAiProxyConfigurationValidator();
        var options = ConfigWith("bedrock-claude", new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.BedrockKind,
            Region = "us-west-2"
        });

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public void GetProvider_ReturnsConfiguredProviderByName()
    {
        var options = new StudioAiProxyConfiguration
        {
            Providers =
            {
                ["claude"] = new StudioAiProxyProviderOptions { Kind = StudioAiProxyConfiguration.AnthropicKind }
            }
        };

        options.GetProvider("claude").Should().NotBeNull();
        options.GetProvider("CLAUDE").Should().NotBeNull("provider names are case-insensitive");
        options.GetProvider("missing").Should().BeNull();
    }

    private static StudioAiProxyConfiguration ConfigWith(string name, StudioAiProxyProviderOptions provider) => new()
    {
        Enabled = true,
        DefaultProvider = name,
        Providers = { [name] = provider }
    };
}
