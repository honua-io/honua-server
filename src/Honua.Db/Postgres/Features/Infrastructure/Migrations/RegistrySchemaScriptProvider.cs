// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp.Engine;
using DbUp.Engine.Transactions;
using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

// Provider migrations can run before registry adoption on a fresh install, or after
// adoption when optional raster support is enabled later. Preserve the historical
// resource while binding its foreign key to the registry's current physical schema.
internal sealed class RegistrySchemaScriptProvider(IScriptProvider source, string registrySchema) : IScriptProvider
{
    private readonly string _quotedSchema = SchemaSearchPath.ValidateAndQuote(registrySchema);

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(Render);

    internal SqlScript Render(SqlScript script)
    {
        if (script.Name != PostgresCoreSchemaGuard.RasterTablesMigration)
        {
            return script;
        }

        var contents = script.Contents.Replace("REFERENCES honua.layers(layer_id)",
            $"REFERENCES {_quotedSchema}.layers(layer_id)", StringComparison.Ordinal);
        // Binding the FK changes the frozen script's hash. Carry its existing review
        // into the rendered script only after validating the original exact bytes;
        // an altered historical resource must still fail the safety guard.
        if (MigrationSafetyClassifier.Classify(script.Name, script.Contents).Classification ==
            MigrationSafetyClassification.ContractAnnotated)
        {
            contents += "\n-- honua:compatibility-review reason=Preserve reviewed raster migration with FK bound to the active registry schema\n";
        }

        return new SqlScript(script.Name, contents, script.SqlScriptOptions);
    }
}
