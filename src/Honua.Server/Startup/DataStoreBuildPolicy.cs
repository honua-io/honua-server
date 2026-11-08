// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Server.Startup;

internal static class DataStoreBuildPolicy
{
    public static bool AdditionalStoresIncluded => DataStoreProviderPolicy.AdditionalStoresIncluded;

    public static string Profile => AdditionalStoresIncluded ? "full" : "postgis";

    public static string[] InstalledProviders => AdditionalStoresIncluded
        ? [DataProviderNames.Postgis, .. DataStoreProviderPolicy.AdditionalStoreSectionsByProvider.Keys.Except(ExcludedBuildProviders)]
        : [DataProviderNames.Postgis];

    public static async Task ValidatePostgisConnectionsAsync(
        ISecureConnectionRegistry registry, IMetadataV2GraphProvider graphProvider)
    {
        var securedConnections = await registry.GetAllConnectionsAsync().ConfigureAwait(false);
        foreach (var connection in securedConnections)
        {
            DataStoreProviderPolicy.EnsurePostgisProvider(connection.Provider);
        }

        var snapshot = await graphProvider.GetCurrentAsync().ConfigureAwait(false);
        foreach (var connection in snapshot.Graph.Connections)
        {
            if (DataStoreProviderPolicy.IsDataStoreConnection(connection))
            {
                DataStoreProviderPolicy.EnsurePostgisProvider(connection.Provider);
            }
        }
    }

    public static void Validate(IConfiguration configuration)
    {
#if HONUA_POSTGIS_ONLY
        ValidatePostgisConfiguration(configuration);
#endif
    }

    public static void ValidatePostgisConfiguration(IConfiguration configuration)
    {
        DataStoreProviderPolicy.EnsurePostgisProvider(configuration["DataSource:Provider"]);

        foreach (var sectionName in DataStoreProviderPolicy.AdditionalStoreSectionsByProvider.Values)
        {
            var section = configuration.GetSection(sectionName);
            if (section.GetValue<bool?>("Enabled") == false)
            {
                continue;
            }

            if (section.AsEnumerable().Any(setting => !string.IsNullOrWhiteSpace(setting.Value)))
            {
                RefuseAdditionalStores();
            }
        }

        if (configuration.GetValue("Experimental:Features:RedshiftProvider", false)
            || configuration.GetValue("Experimental:Features:SnowflakeProvider", false)
            || configuration.GetValue("Experimental:Features:DatabricksProvider", false))
        {
            RefuseAdditionalStores();
        }
    }

    private static readonly string[] ExcludedBuildProviders =
    [
#if HONUA_SKIP_ORACLE
        DataProviderNames.Oracle,
#endif
#if HONUA_SKIP_SNOWFLAKE
        DataProviderNames.Snowflake,
#endif
    ];

    private static void RefuseAdditionalStores() => throw new DataStoreProviderUnavailableException();
}
