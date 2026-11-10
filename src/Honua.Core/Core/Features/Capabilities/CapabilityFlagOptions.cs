// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.Capabilities;

public sealed class CapabilityFlagOptions
{
    public const string SectionName = "Capabilities:Experimental";

    public const string GlobalEnabledKey = "Enabled";

    public const string PerCapabilityEnabledKey = "Enabled";

    public bool Enabled { get; set; }

    public IDictionary<string, ExperimentalCapabilityFlag> Capabilities { get; } =
        new Dictionary<string, ExperimentalCapabilityFlag>(StringComparer.OrdinalIgnoreCase);

    public bool IsExperimentalEnabled(string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);

        if (Enabled)
        {
            return true;
        }

        return Capabilities.TryGetValue(capabilityId, out var flag) && flag.Enabled;
    }

    public static bool IsExperimentalEnabled(IConfiguration configuration, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new CapabilityFlagOptions();
        Bind(options, configuration.GetSection(SectionName));
        return options.IsExperimentalEnabled(capabilityId);
    }

    // The experimental section is bound by hand because the global switch and the per-capability children are siblings the default binder cannot split.
    public static void Bind(CapabilityFlagOptions options, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(section);

        options.Enabled = section.GetValue(GlobalEnabledKey, false);

        foreach (var child in section.GetChildren())
        {
            // Skip the global master scalar; every other child is a per-capability
            // subsection keyed by the descriptor id.
            if (string.Equals(child.Key, GlobalEnabledKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            options.Capabilities[child.Key] = new ExperimentalCapabilityFlag
            {
                Enabled = child.GetValue(PerCapabilityEnabledKey, false),
            };
        }
    }
}

public sealed class ExperimentalCapabilityFlag
{
    public bool Enabled { get; set; }
}
