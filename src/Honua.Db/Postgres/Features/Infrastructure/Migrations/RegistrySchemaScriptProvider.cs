// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp.Engine;
using DbUp.Engine.Transactions;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

// Provider migrations can run before registry adoption on a fresh install, or after
// adoption when optional raster support is enabled later. Preserve the historical
// resource while binding its foreign key to the registry's current physical schema.
internal sealed class RegistrySchemaScriptProvider(IScriptProvider source, string registrySchema) : IScriptProvider
{
    private readonly string _quotedSchema = SchemaSearchPath.ValidateAndQuote(registrySchema);

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(script =>
            script.Name == PostgresCoreSchemaGuard.RasterTablesMigration
                ? new SqlScript(script.Name, script.Contents.Replace(
                    "REFERENCES honua.layers(layer_id)",
                    $"REFERENCES {_quotedSchema}.layers(layer_id)", StringComparison.Ordinal), script.SqlScriptOptions)
                : script);
}
