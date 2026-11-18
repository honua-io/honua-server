// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Startup;

// Only a PostgreSQL/PostGIS primary composes the system database (InfrastructureCompositionRoot). A DuckDB
// or MySQL primary registers no PostgreSQL data source, so a connection string on such a host is not a
// system database, and startup decisions that need one must not assume it is.
internal static class SystemDatabaseComposition
{
    public static bool PrimaryProviderComposesIt(IConfiguration configuration)
        => DataProviderNames.Normalize(configuration.GetValue<string>("DataSource:Provider"))
            is DataProviderNames.Postgis or DataProviderNames.PostgreSql;

    public static bool IsConfigured(IConfiguration configuration)
        => PrimaryProviderComposesIt(configuration)
            && !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"));
}
