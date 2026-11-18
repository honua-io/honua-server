// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Startup;

// The service and layer catalog is stored only in the PostgreSQL/PostGIS system database. A DuckDB or MySQL
// primary composes no catalog store, so a host on one would start and then answer readiness, every
// catalog-bound protocol request and the admin API with an error. It refuses to start instead. The Development
// and Test environments still compose it for contributors and the provider fixtures, which supply a catalog.
internal static class PrimaryCatalogStore
{
    public static string? ProductionRefusal(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsDevelopment()
            || environment.IsEnvironment("Test")
            || SystemDatabaseComposition.PrimaryProviderComposesIt(configuration))
        {
            return null;
        }

        return $"DataSource__Provider '{configuration["DataSource:Provider"]}' cannot run a host outside the Development and Test environments: the "
            + "service and layer catalog is stored only in a PostgreSQL/PostGIS system database. Set "
            + "DataSource__Provider=postgis and ConnectionStrings__DefaultConnection to your PostgreSQL connection string.";
    }
}
