// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;

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

    public static string[] InstalledProviders =>
    [
        DataProviderNames.Postgis,
#if !HONUA_POSTGIS_ONLY
        DataProviderNames.DuckDb,
        DataProviderNames.MySql,
        DataProviderNames.SqlServer,
        DataProviderNames.GeoServicesRest,
        DataProviderNames.Redshift,
        DataProviderNames.Databricks,
#if !HONUA_SKIP_ORACLE
        DataProviderNames.Oracle,
#endif
#if !HONUA_SKIP_SNOWFLAKE
        DataProviderNames.Snowflake,
#endif
#endif
    ];

    public static Task ValidatePostgisConnectionsAsync(
        ISecureConnectionRegistry registry, IMetadataV2GraphProvider graphProvider) => Task.CompletedTask;

    public static void Validate(IConfiguration configuration)
    {
#if HONUA_POSTGIS_ONLY
        ValidatePostgisConfiguration(configuration);
#endif
    }

    public static void ValidatePostgisConfiguration(IConfiguration configuration)
    {
        var primary = DataProviderNames.Normalize(configuration["DataSource:Provider"]);
        if (primary is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql))
        {
            RefuseAdditionalStores();
        }

        foreach (var sectionName in AdditionalStoreSections)
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

    private static readonly string[] AdditionalStoreSections =
        ["DuckDB", "MySql", "SqlServer", "GeoServicesRest", "Oracle", "Redshift", "Snowflake", "Databricks"];

    private static void RefuseAdditionalStores() => throw new InvalidOperationException(
        "This image includes only the PostGIS data store. Use the same-version nightly-jit-<revision>" +
        FullImageTagSuffix + " image for additional data stores; see " + DocumentationHref + ".");
}
