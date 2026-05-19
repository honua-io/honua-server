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
    /// The provider type. Supported values: <c>"openai"</c> (live OpenAI-compatible
    /// chat completion) and <c>"deterministic"</c> (fixture replay; no network).
    /// </summary>
    public string Provider { get; set; } = "openai";

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

        // The deterministic provider has no network surface, so the endpoint,
        // model, and timeout fields are not load-bearing. Leaving the OpenAI
        // validations active for non-OpenAI providers would force operators
        // running the fixture profile to supply throwaway URLs/keys.
        var isOpenAi = string.IsNullOrWhiteSpace(options.Provider)
            || string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase);

        if (isOpenAi)
        {
            if (string.IsNullOrWhiteSpace(options.Endpoint))
            {
                errors.Add("NlQuery:Endpoint cannot be empty");
            }
            else
            {
                ValidateUrl(options.Endpoint, "NlQuery:Endpoint", errors, requireHttps: true);
            }

            ValidateRequiredString(options.Model, "NlQuery:Model", errors);
            ValidateRange(options.TimeoutSeconds, 5, 120, "NlQuery:TimeoutSeconds", errors);
            ValidateRange(options.MaxTokens, 128, 8192, "NlQuery:MaxTokens", errors);
        }
    }
}
