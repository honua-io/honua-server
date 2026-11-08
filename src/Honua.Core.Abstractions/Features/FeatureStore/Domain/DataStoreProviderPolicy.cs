// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

internal static class DataStoreProviderPolicy
{
#if HONUA_POSTGIS_ONLY
    public static bool AdditionalStoresIncluded => false;
#else
    public static bool AdditionalStoresIncluded => true;
#endif

    public const string FullImageTagSuffix = "-full";
    public const string DocumentationHref =
        "https://github.com/honua-io/honua-server/blob/trunk/docs/ops/nightly-images.md";
    public const string RefusalMessage =
        "This image includes only the PostGIS data store. Use the same-version nightly-jit-<revision>" +
        FullImageTagSuffix + " image for additional data stores; see " + DocumentationHref + ".";

    public static bool IsDataStoreConnection(MetadataV2Connection connection) =>
        connection.Type is MetadataV2ConnectionType.Database or MetadataV2ConnectionType.Managed
        || AdditionalStoreSectionsByProvider.ContainsKey(DataProviderNames.Normalize(connection.Provider));

    public static string? StoreProfileRefusalFor(string? provider) => AdditionalStoresIncluded
        || IsPostgisProvider(provider)
            ? null : RefusalMessage;

    public static void EnsureStoreProfileIncludes(string? provider)
    {
        if (!AdditionalStoresIncluded)
            EnsurePostgisProvider(provider);
    }

    public static void EnsurePostgisProvider(string? provider)
    {
        if (!IsPostgisProvider(provider))
            throw new DataStoreProviderUnavailableException();
    }

    private static bool IsPostgisProvider(string? provider) =>
        DataProviderNames.Normalize(provider) is DataProviderNames.Postgis or DataProviderNames.PostgreSql;

    public static readonly IReadOnlyDictionary<string, string> AdditionalStoreSectionsByProvider =
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
}

internal sealed class DataStoreProviderUnavailableException() : InvalidOperationException(
    DataStoreProviderPolicy.RefusalMessage);
