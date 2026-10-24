// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    /// (fixture replay; no network). Server-side model inference was removed
    /// in ADR-0076; the client supplies a <c>FilterPlan</c>.
    /// </summary>
    public string Provider { get; set; } = "deterministic";
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

        if (!string.IsNullOrWhiteSpace(options.Provider)
            && !string.Equals(options.Provider, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"NlQuery:Provider '{options.Provider}' is not supported. The only supported value is "
                + "'deterministic'; server-side model inference was removed in ADR-0076.");
        }
    }
}
