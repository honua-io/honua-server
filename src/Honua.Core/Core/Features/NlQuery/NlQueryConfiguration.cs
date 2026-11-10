// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.NlQuery;

public sealed class NlQueryConfiguration
{
    public const string SectionName = "NlQuery";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = "deterministic";
}

public sealed class NlQueryConfigurationValidator : ConfigurationValidator<NlQueryConfiguration>
{
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
