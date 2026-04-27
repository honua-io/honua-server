// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Reporting;

/// <summary>
/// Configuration for the analysis reporting feature. The deterministic path is
/// always available; LLM narrative enrichment is opt-in via
/// <see cref="ReportingNarrativeConfiguration.Enabled"/>.
/// </summary>
public sealed class ReportingConfiguration
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Reporting";

    /// <summary>
    /// Whether the reporting feature is enabled. Defaults to <c>true</c>; the
    /// deterministic path has no external dependencies and is safe to host.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of rows a <see cref="Domain.TableSection"/> may carry.
    /// Excess rows are summarized via
    /// <see cref="Domain.TableSection.TruncatedRowCount"/>. Default: 500.
    /// </summary>
    public int MaxTableRows { get; set; } = 500;

    /// <summary>Cache configuration for rendered report bodies.</summary>
    public ReportingCacheConfiguration Cache { get; set; } = new();

    /// <summary>Configuration for the optional LLM narrative provider.</summary>
    public ReportingNarrativeConfiguration Narrative { get; set; } = new();
}

/// <summary>Cache configuration for rendered report bodies.</summary>
public sealed class ReportingCacheConfiguration
{
    /// <summary>
    /// Cache TTL for rendered Markdown/HTML bodies, in minutes. The runtime
    /// caps this by the result-package retention TTL so cached reports cannot
    /// outlive their source. Default: 60 minutes.
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum number of <see cref="Domain.AnalysisReport"/> envelopes kept in
    /// the in-memory store. Excess entries are evicted by the underlying
    /// <see cref="Microsoft.Extensions.Caching.Memory.MemoryCache"/> so a
    /// long-lived server cannot grow unbounded as new job ids accumulate.
    /// Default: 1024.
    /// </summary>
    public int MaxEntries { get; set; } = 1024;
}

/// <summary>LLM narrative provider configuration.</summary>
public sealed class ReportingNarrativeConfiguration
{
    /// <summary>
    /// Token accepted by <see cref="Provider"/>. Validated case-insensitively
    /// when <see cref="Enabled"/> is true.
    /// </summary>
    public const string ProviderOpenAi = "openai";

    /// <summary>
    /// Whether the LLM narrative provider is enabled. Default: <c>false</c> —
    /// reports stay deterministic until explicitly enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Provider type. Currently only <c>openai</c> is supported.</summary>
    public string Provider { get; set; } = ProviderOpenAi;

    /// <summary>
    /// API endpoint base URL. Must follow the OpenAI <c>/v1/chat/completions</c>
    /// convention. Works with OpenAI-compatible proxies (Ollama, vLLM, LiteLLM).
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>Model identifier to use for narrative enrichment.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// API key for the provider. May also be sourced from the
    /// <c>HONUA_REPORTING_NARRATIVE_API_KEY</c> environment variable.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Maximum tokens for the model response.</summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Cache TTL for hash-keyed narrative responses in hours.</summary>
    public int CacheTtlHours { get; set; } = 24;
}

/// <summary>
/// Validates <see cref="ReportingConfiguration"/> at startup. Narrative-block
/// validation only runs when <see cref="ReportingNarrativeConfiguration.Enabled"/>
/// is true so deployments that never enable LLM enrichment do not need to
/// supply provider details.
/// </summary>
public sealed class ReportingConfigurationValidator : ConfigurationValidator<ReportingConfiguration>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(ReportingConfiguration options, List<string> errors)
    {
        ValidateRange(options.MaxTableRows, 1, 100_000, "Reporting:MaxTableRows", errors);
        ValidateRange(options.Cache.TtlMinutes, 1, 24 * 60, "Reporting:Cache:TtlMinutes", errors);
        ValidateRange(options.Cache.MaxEntries, 1, 100_000, "Reporting:Cache:MaxEntries", errors);

        if (!options.Narrative.Enabled)
        {
            return;
        }

        ValidateRequiredString(options.Narrative.Provider, "Reporting:Narrative:Provider", errors);

        if (!string.IsNullOrWhiteSpace(options.Narrative.Provider) &&
            !string.Equals(options.Narrative.Provider, ReportingNarrativeConfiguration.ProviderOpenAi, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Reporting:Narrative:Provider '{options.Narrative.Provider}' is not supported. Only '{ReportingNarrativeConfiguration.ProviderOpenAi}' is recognized.");
        }

        if (string.IsNullOrWhiteSpace(options.Narrative.Endpoint))
        {
            errors.Add("Reporting:Narrative:Endpoint cannot be empty when Narrative.Enabled is true.");
        }
        else
        {
            ValidateUrl(options.Narrative.Endpoint, "Reporting:Narrative:Endpoint", errors, requireHttps: true);
        }

        ValidateRequiredString(options.Narrative.Model, "Reporting:Narrative:Model", errors);
        ValidateRange(options.Narrative.TimeoutSeconds, 5, 120, "Reporting:Narrative:TimeoutSeconds", errors);
        ValidateRange(options.Narrative.MaxTokens, 128, 8192, "Reporting:Narrative:MaxTokens", errors);
        ValidateRange(options.Narrative.CacheTtlHours, 1, 24 * 7, "Reporting:Narrative:CacheTtlHours", errors);
    }
}
