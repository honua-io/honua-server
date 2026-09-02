// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Org.BouncyCastle.Crypto.Parameters;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Configuration for the provider-agnostic Studio AI proxy (honua-server#3000). Operators declare
/// any number of arbitrarily-named providers, each pointing at one of the three adapter
/// <see cref="StudioAiProxyProviderOptions.Kind"/> values. This is what makes OpenRouter, LiteLLM,
/// Ollama, and vLLM all reachable through the single <c>openai</c> kind — they differ only by
/// <see cref="StudioAiProxyProviderOptions.Endpoint"/>.
/// </summary>
public sealed class StudioAiProxyConfiguration
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "StudioAiProxy";

    /// <summary>Adapter kind for the Anthropic Messages API.</summary>
    public const string AnthropicKind = "anthropic";

    /// <summary>Adapter kind for any OpenAI-compatible <c>/chat/completions</c> endpoint.</summary>
    public const string OpenAiKind = "openai";

    /// <summary>Adapter kind for AWS Bedrock (bridges the existing Bedrock chat-client plumbing).</summary>
    public const string BedrockKind = "bedrock";

    /// <summary>Default AWS region for a <see cref="BedrockKind"/> provider when none is configured.</summary>
    public const string DefaultBedrockRegion = "us-west-2";

    /// <summary>Whether the feature is enabled. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>The provider name used when a chat/capabilities request does not name one.</summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>
    /// Maximum accepted prompt size across all message content, in UTF-16 characters. Oversized
    /// requests are rejected before any provider is called.
    /// </summary>
    public int MaxPromptCharacters { get; set; } = 32_000;

    /// <summary>Maximum UTF-8 bytes accepted by the Studio AI chat HTTP route.</summary>
    public int MaxRequestBytes { get; set; } = 1_048_576;

    /// <summary>Maximum serialized provider-event bytes accepted for one response.</summary>
    public int MaxResponseBytes { get; set; } = 4_194_304;

    /// <summary>Maximum serialized bytes accepted for one provider event.</summary>
    public int MaxEventBytes { get; set; } = 262_144;

    /// <summary>Maximum provider events accepted for one response.</summary>
    public int MaxResponseEventCount { get; set; } = 4_096;

    /// <summary>Maximum UTF-8 argument bytes accumulated for one streamed tool call.</summary>
    public int MaxToolArgumentBytes { get; set; } = 262_144;

    /// <summary>Operator-named provider blocks.</summary>
    public Dictionary<string, StudioAiProxyProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transcript-provenance signing policy.</summary>
    public StudioAiTranscriptSigningOptions TranscriptSigning { get; set; } = new();

    /// <summary>Returns the configured options for the named provider, or null when absent.</summary>
    public StudioAiProxyProviderOptions? GetProvider(string name)
        => Providers.TryGetValue(name, out var options) ? options : null;
}

/// <summary>Ed25519 signing configuration. Private material is accepted only by reference.</summary>
public sealed class StudioAiTranscriptSigningOptions
{
    /// <summary>Stable signer identity placed in every envelope.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Secret-provider reference resolving to a base64-encoded 32-byte Ed25519 seed.</summary>
    public string PrivateKeyReference { get; set; } = string.Empty;

    /// <summary>Validity of a newly issued transcript.</summary>
    public int LifetimeSeconds { get; set; } = 900;

    /// <summary>Previous/future public keys published during rotation overlap.</summary>
    public List<StudioAiTranscriptVerificationKeyOptions> OverlapKeys { get; set; } = [];
}

/// <summary>Configured public key retained for an explicit verification overlap window.</summary>
public sealed class StudioAiTranscriptVerificationKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>Per-provider connection options for the Studio AI proxy.</summary>
public sealed class StudioAiProxyProviderOptions
{
    /// <summary>
    /// Which adapter this provider routes through: <see cref="StudioAiProxyConfiguration.AnthropicKind"/>,
    /// <see cref="StudioAiProxyConfiguration.OpenAiKind"/>, or <see cref="StudioAiProxyConfiguration.BedrockKind"/>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// API base URL. For the <c>openai</c> kind this is the base that <c>/chat/completions</c> is
    /// appended to (so it covers OpenRouter/LiteLLM/Ollama/vLLM/LM Studio via config alone). For
    /// <c>anthropic</c> it is the Messages API base. Not used by <c>bedrock</c>, which targets the
    /// regional Bedrock runtime endpoint derived from <see cref="Region"/>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The model identifier to use.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// AWS region for a <c>bedrock</c> provider (for example <c>us-west-2</c>). Ignored by the other
    /// kinds. When empty, the Bedrock adapter falls back to <see cref="StudioAiProxyConfiguration.DefaultBedrockRegion"/>.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// API key for the provider, or a secret reference resolved via <c>ISecretProvider</c>. May also
    /// be supplied via a per-provider environment variable (for example
    /// <c>HONUA_STUDIOAI_MYPROVIDER_API_KEY</c>). A local <c>openai</c>-kind endpoint (Ollama/vLLM)
    /// typically needs none; a <c>bedrock</c> provider typically relies on the AWS credential chain
    /// instead and needs no key either.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>HTTP request timeout in seconds, applied as a ceiling over the whole streamed call.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum tokens for the model response when a request does not override it.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Whether this provider is willing to accept tool/function-call definitions. All three adapters
    /// support tools; this exists so an operator can pin a model known not to tool-call reliably and
    /// have that reflected honestly in <c>GET /api/v1/studio/ai/capabilities</c>.
    /// </summary>
    public bool SupportsTools { get; set; } = true;
}

/// <summary>
/// Validates <see cref="StudioAiProxyConfiguration"/> at startup. Every declared provider is
/// validated in full (not just the default) — unlike a single-default-slot feature, every entry an
/// operator adds here is presumed intended for use (Studio clients can name it explicitly).
/// </summary>
public sealed class StudioAiProxyConfigurationValidator : ConfigurationValidator<StudioAiProxyConfiguration>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(StudioAiProxyConfiguration options, List<string> errors)
    {
        if (!options.Enabled)
        {
            return;
        }

        ValidateRequiredString(options.DefaultProvider, "StudioAiProxy:DefaultProvider", errors);
        ValidateRange(options.MaxPromptCharacters, 1, 500_000, "StudioAiProxy:MaxPromptCharacters", errors);
        ValidateRange(options.MaxRequestBytes, 1, 16_777_216, "StudioAiProxy:MaxRequestBytes", errors);
        ValidateRange(options.MaxResponseBytes, 1, 67_108_864, "StudioAiProxy:MaxResponseBytes", errors);
        ValidateRange(options.MaxEventBytes, 1, options.MaxResponseBytes, "StudioAiProxy:MaxEventBytes", errors);
        ValidateRange(options.MaxResponseEventCount, 1, 65_536, "StudioAiProxy:MaxResponseEventCount", errors);
        ValidateRange(options.MaxToolArgumentBytes, 1, options.MaxResponseBytes, "StudioAiProxy:MaxToolArgumentBytes", errors);

        if (options.Providers.Count == 0)
        {
            errors.Add("StudioAiProxy:Providers must declare at least one provider when StudioAiProxy:Enabled is true.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultProvider) && options.GetProvider(options.DefaultProvider) is null)
        {
            errors.Add($"StudioAiProxy:DefaultProvider '{options.DefaultProvider}' does not match any entry under StudioAiProxy:Providers.");
        }

        foreach (var (name, provider) in options.Providers)
        {
            ValidateProvider(name, provider, errors);
        }

        var signing = options.TranscriptSigning;
        if (!string.IsNullOrWhiteSpace(signing.PrivateKeyReference) || !string.IsNullOrWhiteSpace(signing.KeyId))
        {
            ValidateRequiredString(signing.KeyId, "StudioAiProxy:TranscriptSigning:KeyId", errors);
            ValidateRequiredString(signing.PrivateKeyReference, "StudioAiProxy:TranscriptSigning:PrivateKeyReference", errors);
            ValidateRange(signing.LifetimeSeconds, 30, 86_400, "StudioAiProxy:TranscriptSigning:LifetimeSeconds", errors);
            if (!string.IsNullOrWhiteSpace(signing.PrivateKeyReference)
                && !signing.PrivateKeyReference.Contains("://", StringComparison.Ordinal))
            {
                errors.Add("StudioAiProxy:TranscriptSigning:PrivateKeyReference must be a secret reference, not inline key material.");
            }
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(signing.KeyId))
        {
            keyIds.Add(signing.KeyId);
        }

        for (var index = 0; index < signing.OverlapKeys.Count; index++)
        {
            var overlap = signing.OverlapKeys[index];
            var prefix = $"StudioAiProxy:TranscriptSigning:OverlapKeys:{index}";
            ValidateRequiredString(overlap.KeyId, $"{prefix}:KeyId", errors);
            if (!string.IsNullOrWhiteSpace(overlap.KeyId) && !keyIds.Add(overlap.KeyId))
            {
                errors.Add($"{prefix}:KeyId '{overlap.KeyId}' is duplicated.");
            }

            byte[] publicKey;
            try
            {
                publicKey = Convert.FromBase64String(overlap.PublicKey ?? string.Empty);
            }
            catch (FormatException)
            {
                errors.Add($"{prefix}:PublicKey must be valid Base64.");
                continue;
            }

            if (publicKey.Length != Ed25519PublicKeyParameters.KeySize)
            {
                errors.Add($"{prefix}:PublicKey must decode to {Ed25519PublicKeyParameters.KeySize} bytes.");
            }

            if (overlap.NotBefore is null)
            {
                errors.Add($"{prefix}:NotBefore is required to define the rotation overlap window.");
            }

            if (overlap.NotAfter is null)
            {
                errors.Add($"{prefix}:NotAfter is required to define the rotation overlap window.");
            }

            if (overlap.NotBefore is { } notBefore && overlap.NotAfter is { } notAfter && notBefore >= notAfter)
            {
                errors.Add($"{prefix}:NotBefore must be earlier than NotAfter.");
            }
        }
    }

    private static void ValidateProvider(string name, StudioAiProxyProviderOptions provider, List<string> errors)
    {
        var prefix = $"StudioAiProxy:Providers:{name}";

        var kind = (provider.Kind ?? string.Empty).Trim();
        var isKnownKind =
            string.Equals(kind, StudioAiProxyConfiguration.AnthropicKind, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, StudioAiProxyConfiguration.OpenAiKind, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, StudioAiProxyConfiguration.BedrockKind, StringComparison.OrdinalIgnoreCase);

        if (!isKnownKind)
        {
            errors.Add($"{prefix}:Kind '{provider.Kind}' is not a supported adapter kind. Supported values: 'anthropic', 'openai', 'bedrock'.");
            return;
        }

        ValidateRange(provider.TimeoutSeconds, 5, 600, $"{prefix}:TimeoutSeconds", errors);
        ValidateRange(provider.MaxTokens, 256, 65_536, $"{prefix}:MaxTokens", errors);

        // Bedrock targets the regional Bedrock runtime endpoint via the AWS credential chain (IAM),
        // so it requires no endpoint URL or API key — only a model id. Region is optional.
        if (string.Equals(kind, StudioAiProxyConfiguration.BedrockKind, StringComparison.OrdinalIgnoreCase))
        {
            ValidateRequiredString(provider.Model, $"{prefix}:Model", errors);
            return;
        }

        // anthropic must be HTTPS; openai-compatible may point at a plain-HTTP localhost endpoint
        // (Ollama/vLLM/LM Studio), so only require HTTPS for the hosted Anthropic API shape.
        var requireHttps = string.Equals(kind, StudioAiProxyConfiguration.AnthropicKind, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            errors.Add($"{prefix}:Endpoint cannot be empty");
        }
        else
        {
            ValidateUrl(provider.Endpoint, $"{prefix}:Endpoint", errors, requireHttps);
        }

        ValidateRequiredString(provider.Model, $"{prefix}:Model", errors);
    }
}
