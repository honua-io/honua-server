// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Server.Startup;

internal static class DataStoreBuildPolicy
{
#if HONUA_POSTGIS_ONLY
    public static bool AdditionalStoresIncluded => false;
#else
    public static bool AdditionalStoresIncluded => true;
#endif

    public const string FullImageTagSuffix = "-full";
    public const string DocumentationHref =
        "https://github.com/honua-io/honua-server/blob/trunk/docs/ops/nightly-images.md";

    public static string Profile => AdditionalStoresIncluded ? "full" : "postgis";

    public static string[] InstalledProviders => AdditionalStoresIncluded
        ? [DataProviderNames.Postgis, .. AdditionalStoreSectionsByProvider.Keys.Except(ExcludedBuildProviders)]
        : [DataProviderNames.Postgis];

    public static async Task ValidatePostgisConnectionsAsync(
        ISecureConnectionRegistry registry, IMetadataV2GraphProvider graphProvider)
    {
        var securedConnections = await registry.GetAllConnectionsAsync().ConfigureAwait(false);
        foreach (var connection in securedConnections)
        {
            ValidatePostgisProvider(connection.Provider);
        }

        var snapshot = await graphProvider.GetCurrentAsync().ConfigureAwait(false);
        foreach (var connection in snapshot.Graph.Connections)
        {
            if (connection.Type is MetadataV2ConnectionType.Database or MetadataV2ConnectionType.Managed
                || AdditionalStoreSectionsByProvider.ContainsKey(DataProviderNames.Normalize(connection.Provider)))
            {
                ValidatePostgisProvider(connection.Provider);
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
        ValidatePostgisProvider(configuration["DataSource:Provider"]);

        foreach (var sectionName in AdditionalStoreSectionsByProvider.Values)
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

    private static readonly IReadOnlyDictionary<string, string> AdditionalStoreSectionsByProvider =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DataProviderNames.DuckDb] = "DuckDB",
            [DataProviderNames.MySql] = "MySql",
            [DataProviderNames.SqlServer] = "SqlServer",
            [DataProviderNames.GeoServicesRest] = "GeoServicesRest",
            [DataProviderNames.Redshift] = "Redshift",
            [DataProviderNames.Databricks] = "Databricks",
            [DataProviderNames.Oracle] = "Oracle",
            [DataProviderNames.Snowflake] = "Snowflake"
        };

    private static readonly string[] ExcludedBuildProviders =
    [
#if HONUA_SKIP_ORACLE
        DataProviderNames.Oracle,
#endif
#if HONUA_SKIP_SNOWFLAKE
        DataProviderNames.Snowflake,
#endif
    ];

    private static void ValidatePostgisProvider(string? provider)
    {
        if (DataProviderNames.Normalize(provider) is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql))
        {
            RefuseAdditionalStores();
        }
    }

    private static void RefuseAdditionalStores() => throw new ExcludedDataStoreException();
}

internal sealed class ExcludedDataStoreException() : InvalidOperationException(
    "This image includes only the PostGIS data store. Use the same-version nightly-jit-<revision>" +
    DataStoreBuildPolicy.FullImageTagSuffix + " image for additional data stores; see " + DataStoreBuildPolicy.DocumentationHref + ".");
