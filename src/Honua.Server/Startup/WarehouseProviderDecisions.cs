// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Startup;

internal sealed record WarehouseProviderDecision(
    string CapabilityId,
    string ProviderName,
    string ConfigurationSection,
    string LegacyGateKey,
    bool Supported,
    bool Enabled);

/// <summary>
/// Canonical, process-lifetime warehouse enablement decisions shared by startup
/// composition and capability discovery. Legacy experimental keys are inputs to
/// this resolver only; consumers never re-read configuration independently.
/// </summary>
internal sealed class WarehouseProviderDecisions
{
    public WarehouseProviderDecisions(IConfiguration configuration)
    {
        Redshift = Resolve(configuration, "provider.redshift", DataProviderNames.Redshift, "Redshift",
            "Experimental:Features:RedshiftProvider", supported: true);
#if HONUA_SKIP_SNOWFLAKE
        Snowflake = Resolve(configuration, "provider.snowflake", DataProviderNames.Snowflake, "Snowflake",
            "Experimental:Features:SnowflakeProvider", supported: false);
#else
        Snowflake = Resolve(configuration, "provider.snowflake", DataProviderNames.Snowflake, "Snowflake",
            "Experimental:Features:SnowflakeProvider", supported: true);
#endif
        Databricks = Resolve(configuration, "provider.databricks", DataProviderNames.Databricks, "Databricks",
            "Experimental:Features:DatabricksProvider", supported: true);
        All = [Redshift, Snowflake, Databricks];
    }

    public WarehouseProviderDecision Redshift { get; }
    public WarehouseProviderDecision Snowflake { get; }
    public WarehouseProviderDecision Databricks { get; }
    public IReadOnlyList<WarehouseProviderDecision> All { get; }

    private static WarehouseProviderDecision Resolve(
        IConfiguration configuration,
        string capabilityId,
        string providerName,
        string section,
        string legacyGateKey,
        bool supported)
    {
        var gateEnabled = configuration.GetValue(legacyGateKey, false);
        var explicitlyEnabled = string.Equals(configuration[$"{section}:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        if (!gateEnabled && explicitlyEnabled)
        {
            throw new InvalidOperationException(
                $"Configuration conflict: {section}:Enabled is set to true but the experimental gate " +
                $"'{legacyGateKey}' is not enabled. Enable the gate to opt in, or set {section}:Enabled=false.");
        }

        var providerEnabled = configuration.GetValue($"{section}:Enabled", true);
        return new WarehouseProviderDecision(
            capabilityId,
            providerName,
            section,
            legacyGateKey,
            supported,
            supported && gateEnabled && providerEnabled);
    }
}
