// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Org.BouncyCastle.Crypto.Parameters;

namespace Honua.Ai.StudioAiProxy;

public sealed class StudioAiProxyConfiguration
{
    public Honua.Core.Features.Security.AiDisclosurePolicyOptions Disclosure { get; set; } = new();

    public const string SectionName = "StudioAiProxy";

    public const string AnthropicKind = "anthropic";

    public const string OpenAiKind = "openai";

    public const string BedrockKind = "bedrock";

    public const string DefaultBedrockRegion = "us-east-1";

    public bool Enabled { get; set; }

    public string DefaultProvider { get; set; } = string.Empty;

    public int MaxPromptCharacters { get; set; } = 128_000;

    public int MaxRequestBytes { get; set; } = 1_048_576;

    public int MaxResponseBytes { get; set; } = 4_194_304;

    public int MaxEventBytes { get; set; } = 262_144;

    public int MaxResponseEventCount { get; set; } = 4_096;

    public int MaxToolArgumentBytes { get; set; } = 262_144;

    public Dictionary<string, StudioAiProxyProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public StudioAiTranscriptSigningOptions TranscriptSigning { get; set; } = new();

    public StudioAiProxyProviderOptions? GetProvider(string name)
        => Providers.TryGetValue(name, out var options) ? options : null;
}

public sealed class StudioAiTranscriptSigningOptions
{
    public string KeyId { get; set; } = string.Empty;

    // The reference resolves to a base64-encoded 32-byte Ed25519 seed.
    public string PrivateKeyReference { get; set; } = string.Empty;

    public int LifetimeSeconds { get; set; } = 900;

    public List<StudioAiTranscriptVerificationKeyOptions> OverlapKeys { get; set; } = [];
}

public sealed class StudioAiTranscriptVerificationKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
    public bool Revoked { get; set; }
}

public sealed class StudioAiProxyProviderOptions
{
    public string Kind { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    // Set only by the environment fallback in PostConfigure so the resolver can distinguish a
    // trusted environment-bound credential from a hosted inline literal.
    internal bool ApiKeyFromEnvironment { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxTokens { get; set; } = 4096;

    public bool SupportsTools { get; set; } = true;
}

public sealed class StudioAiProxyConfigurationValidator : ConfigurationValidator<StudioAiProxyConfiguration>
{
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
                && !IsTranscriptSigningReference(signing.PrivateKeyReference))
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

    private static bool IsTranscriptSigningReference(string reference)
    {
        if (reference.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        // The built-in AWS resolver uses provider:service:identifier, not a URI authority.
        // Runtime provider resolution and key validation still happen in the transcript signer.
        const string awsPrefix = "aws:secretsmanager:";
        return reference.StartsWith(awsPrefix, StringComparison.OrdinalIgnoreCase)
            && reference.Length > awsPrefix.Length
            && reference[awsPrefix.Length] != '?'
            && !reference.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));
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

        // Local OpenAI-compatible runtimes may use HTTP. Every remote provider must use
        // HTTPS, including private-network hosts; local development is a loopback exception.
        var allowLocalHttp = string.Equals(kind, StudioAiProxyConfiguration.OpenAiKind, StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.IsLoopback;
        var requireHttps = !allowLocalHttp;

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
