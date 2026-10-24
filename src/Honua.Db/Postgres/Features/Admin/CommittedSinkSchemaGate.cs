// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Features.Admin;

/// <summary>
/// Operational-schema checks plus the pinned committed-sink exception.
/// </summary>
internal static class CommittedSinkSchemaGate
{
    internal static bool Allows(PostgresSchemaConfiguration configuration, string? currentSchema, string? schema)
    {
        if (configuration.IsOperationalSchema(schema, currentSchema))
        {
            return true;
        }

        return CommittedExternalSchemaPublication.AllowsUnlisted(
            schema,
            ImportTargetSchemaPolicy.IsReserved(schema, configuration.MetadataSchemas),
            schema is not null && SchemaSearchPath.IsValidIdentifier(schema));
    }

    internal static IReadOnlyList<string> DiscoverySchemas(
        PostgresSchemaConfiguration configuration,
        string? currentSchema,
        string schema)
    {
        var schemas = configuration.ResolveDiscoverySchemas(currentSchema);
        if (!CommittedExternalSchemaPublication.IsActive ||
            schemas.Contains(schema, StringComparer.OrdinalIgnoreCase) ||
            !Allows(configuration, currentSchema, schema))
        {
            return schemas;
        }

        var expanded = new string[schemas.Count + 1];
        for (var index = 0; index < schemas.Count; index++)
        {
            expanded[index] = schemas[index];
        }

        expanded[^1] = schema;
        return expanded;
    }
}
