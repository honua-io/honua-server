// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.WorkflowPackages.Generation;

/// <summary>
/// Configuration for the natural-language workflow-package generation feature.
/// Modeled on <c>NlQueryConfiguration</c> but multi-provider: a default provider plus a
/// per-provider block keyed by provider id (<c>local</c>, <c>openai</c>, <c>anthropic</c>,
/// <c>deterministic</c>).
/// </summary>
public sealed class WorkflowGenerationConfiguration
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "WorkflowGeneration";

    /// <summary>Stable id for the OpenAI-compatible "local" provider.</summary>
    public const string LocalProviderId = "local";

    /// <summary>Stable id for the OpenAI provider.</summary>
    public const string OpenAiProviderId = "openai";

    /// <summary>Stable id for the Anthropic (Claude) provider.</summary>
    public const string AnthropicProviderId = "anthropic";

    /// <summary>Stable id for the deterministic fixture-replay provider.</summary>
    public const string DeterministicProviderId = "deterministic";

    /// <summary>
    /// Whether the feature is enabled. Default: false. When disabled the endpoint reports
    /// the feature as unavailable and the providers endpoint returns <c>enabled:false</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The default provider id used when a generate request does not name one.
    /// Supported: <c>local</c>, <c>openai</c>, <c>anthropic</c>, <c>deterministic</c>.
    /// </summary>
    public string DefaultProvider { get; set; } = LocalProviderId;

    /// <summary>
    /// Maximum number of bounded repair re-prompts when the generated graph fails the
    /// validation gate before the turn falls back to <c>needs-clarification</c>/<c>error</c>.
    /// </summary>
    public int MaxRepairAttempts { get; set; } = 1;

    /// <summary>
    /// Per-provider configuration blocks keyed by provider id.
    /// </summary>
    public Dictionary<string, WorkflowGenerationProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the configured options for the supplied provider id, or null when absent.
    /// </summary>
    public WorkflowGenerationProviderOptions? GetProvider(string providerId)
        => Providers.TryGetValue(providerId, out var options) ? options : null;
}

/// <summary>
/// Per-provider connection options for workflow generation.
/// </summary>
public sealed class WorkflowGenerationProviderOptions
{
    /// <summary>
    /// API endpoint base URL. For OpenAI-compatible providers this follows the
    /// <c>/v1/chat/completions</c> convention. For Anthropic it is the Messages API base.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The model identifier to use.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// API key for the provider, or a secret reference resolved via <c>ISecretProvider</c>.
    /// May also be supplied via a per-provider environment variable
    /// (for example <c>HONUA_WORKFLOWGEN_OPENAI_API_KEY</c>).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum tokens for the model response.</summary>
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>
/// Validates <see cref="WorkflowGenerationConfiguration"/> at startup. Only the default
/// provider's network-facing fields are validated, mirroring NlQuery's conditional pattern
/// so operators are not forced to supply throwaway URLs/keys for unused providers.
/// </summary>
public sealed class WorkflowGenerationConfigurationValidator : ConfigurationValidator<WorkflowGenerationConfiguration>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(WorkflowGenerationConfiguration options, List<string> errors)
    {
        if (!options.Enabled)
        {
            // Disabled feature: do not force any provider configuration.
            return;
        }

        ValidateRequiredString(options.DefaultProvider, "WorkflowGeneration:DefaultProvider", errors);
        ValidateRange(options.MaxRepairAttempts, 0, 5, "WorkflowGeneration:MaxRepairAttempts", errors);

        var provider = (options.DefaultProvider ?? string.Empty).Trim();
        var isKnown = provider.Length == 0
            || string.Equals(provider, WorkflowGenerationConfiguration.LocalProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.OpenAiProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.AnthropicProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.DeterministicProviderId, StringComparison.OrdinalIgnoreCase);

        if (!isKnown)
        {
            errors.Add($"WorkflowGeneration:DefaultProvider '{provider}' is not a supported provider id. "
                + "Supported values: 'local', 'openai', 'anthropic', 'deterministic'.");
            return;
        }

        // The deterministic provider has no network surface and needs no endpoint/model/key.
        if (string.Equals(provider, WorkflowGenerationConfiguration.DeterministicProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var providerOptions = options.GetProvider(provider);
        if (providerOptions is null)
        {
            errors.Add($"WorkflowGeneration:Providers:{provider} must be configured because it is the default provider.");
            return;
        }

        // local points at localhost (Ollama/vLLM/LiteLLM) and need not be HTTPS; the hosted
        // providers must use HTTPS.
        var requireHttps = !string.Equals(provider, WorkflowGenerationConfiguration.LocalProviderId, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(providerOptions.Endpoint))
        {
            errors.Add($"WorkflowGeneration:Providers:{provider}:Endpoint cannot be empty");
        }
        else
        {
            ValidateUrl(providerOptions.Endpoint, $"WorkflowGeneration:Providers:{provider}:Endpoint", errors, requireHttps);
        }

        ValidateRequiredString(providerOptions.Model, $"WorkflowGeneration:Providers:{provider}:Model", errors);
        ValidateRange(providerOptions.TimeoutSeconds, 5, 300, $"WorkflowGeneration:Providers:{provider}:TimeoutSeconds", errors);
        ValidateRange(providerOptions.MaxTokens, 256, 32_768, $"WorkflowGeneration:Providers:{provider}:MaxTokens", errors);
    }
}
