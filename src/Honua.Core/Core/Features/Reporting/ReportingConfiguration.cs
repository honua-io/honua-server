// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Reporting;

public sealed class ReportingConfiguration
{
    public const string SectionName = "Reporting";

    public bool Enabled { get; set; } = true;

    public int MaxTableRows { get; set; } = 500;

    public ReportingCacheConfiguration Cache { get; set; } = new();

    public ReportingNarrativeConfiguration Narrative { get; set; } = new();
}

public sealed class ReportingCacheConfiguration
{
    // The rendered-body cache TTL is capped by result-package retention so a cached report cannot outlive its source.
    public int TtlMinutes { get; set; } = 60;

    public int MaxEntries { get; set; } = 1024;
}

public sealed class ReportingNarrativeConfiguration
{
    public Honua.Core.Features.Security.AiDisclosurePolicyOptions Disclosure { get; set; } = new();

    public const string ProviderOpenAi = "openai";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = ProviderOpenAi;

    // Endpoint is a base URL, such as https://api.openai.com/v1. The provider appends /chat/completions, including for an OpenAI-compatible proxy.
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4o-mini";

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 15;

    public int MaxTokens { get; set; } = 1024;

    public int CacheTtlHours { get; set; } = 24;
}

// Narrative-provider validation runs only when enrichment is enabled, so a deployment that never enables it need not supply provider details.
public sealed class ReportingConfigurationValidator : ConfigurationValidator<ReportingConfiguration>
{
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
