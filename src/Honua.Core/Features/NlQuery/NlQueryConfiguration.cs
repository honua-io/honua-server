// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.NlQuery;

/// <summary>
/// Configuration for the natural-language spatial query feature.
/// </summary>
public sealed class NlQueryConfiguration
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "NlQuery";

    /// <summary>
    /// Whether the NL query feature is enabled. Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The provider type. The only supported value is <c>"deterministic"</c>
    /// (fixture replay; no network).
    /// </summary>
    /// <remarks>
    /// The <c>"openai"</c> provider was removed in ADR-0076. Planning a
    /// <c>FilterPlan</c> from natural language is the client's job; the server
    /// validates and compiles the plan it is handed. The endpoint, model, key,
    /// timeout, and token settings below are retained only so an existing
    /// configuration section does not fail to bind, and are no longer read.
    /// </remarks>
    public string Provider { get; set; } = "deterministic";

    /// <summary>
    /// The API endpoint URL. Must follow the OpenAI <c>/v1/chat/completions</c> convention.
    /// Works with OpenAI, Ollama, vLLM, LiteLLM, and other OpenAI-compatible backends.
    /// Azure OpenAI's native endpoint format is not supported; use an OpenAI-compatible proxy.
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// The model identifier to use (e.g., "gpt-4o").
    /// </summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>
    /// API key for the provider. Can also be set via HONUA_NLQUERY_API_KEY environment variable.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum tokens for the model response.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;
}

/// <summary>
/// Validates <see cref="NlQueryConfiguration"/> at startup.
/// </summary>
public sealed class NlQueryConfigurationValidator : ConfigurationValidator<NlQueryConfiguration>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(NlQueryConfiguration options, List<string> errors)
    {
        ValidateRequiredString(options.Provider, "NlQuery:Provider", errors);

        // Only the deterministic provider remains (ADR-0076), and it has no
        // network surface, so the endpoint/model/timeout/token settings are no
        // longer load-bearing and are deliberately not validated. Validating
        // them would force operators to supply throwaway URLs and keys for a
        // provider that never opens a socket.
        if (!string.IsNullOrWhiteSpace(options.Provider)
            && !string.Equals(options.Provider, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"NlQuery:Provider '{options.Provider}' is not supported. The only supported value is "
                + "'deterministic'; server-side model inference was removed in ADR-0076.");
        }
    }
}
