// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

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
    /// The provider type. Currently only "openai" is supported.
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
public sealed class NlQueryConfigurationValidator : OptionsValidator<NlQueryConfiguration>
{
    /// <inheritdoc />
    protected override void ValidateOptions(NlQueryConfiguration options, List<string> failures)
    {
        ValidateRequiredString(options.Provider, "NlQuery:Provider", failures);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            failures.Add("NlQuery:Endpoint cannot be empty");
        }
        else
        {
            ValidateUrl(options.Endpoint, "NlQuery:Endpoint", failures, requireHttps: false);
        }

        ValidateRequiredString(options.Model, "NlQuery:Model", failures);
        ValidateRange(options.TimeoutSeconds, 5, 120, "NlQuery:TimeoutSeconds", failures);
        ValidateRange(options.MaxTokens, 128, 8192, "NlQuery:MaxTokens", failures);
    }
}
