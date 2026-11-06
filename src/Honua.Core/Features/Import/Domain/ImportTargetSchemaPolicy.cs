// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.Import.Domain;

// A target must be an operational schema; the metadata schema and PostgreSQL system schemas are refused even if listed.
public static class ImportTargetSchemaPolicy
{
    public const string DefaultMetadataSchema = "honua";

    public const string DefaultOperationalSchema = "honua_data";

    public const string MetadataSchemaConfigurationKey = "Database:Schema";

    public const string DefaultOperationalSchemaConfigurationKey = "Database:DefaultOperationalSchema";

    public const string OperationalSchemasConfigurationKey = "Database:OperationalSchemas";

    public const string NotOperationalSchemaMessage =
        "Target schema is not a configured operational schema. Import targets are limited to the default operational schema, public, and Database:OperationalSchemas.";

    public const string ReservedSchemaMessage =
        "Schema is reserved for server metadata or database system catalogs and cannot be used by an import.";

    public static bool IsReserved(string? schema, IEnumerable<string?>? metadataSchemas)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return false;
        }

        var candidate = schema.Trim();

        // PostgreSQL reserves the pg_ prefix for system schemas (pg_catalog, pg_toast, pg_temp_N).
        if (candidate.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "information_schema", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, DefaultMetadataSchema, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsSchema(metadataSchemas, candidate);
    }

    public static bool IsAllowed(
        string? schema,
        IEnumerable<string?> operationalSchemas,
        IEnumerable<string?>? metadataSchemas)
    {
        ArgumentNullException.ThrowIfNull(operationalSchemas);

        if (string.IsNullOrWhiteSpace(schema))
        {
            return true;
        }

        return !IsReserved(schema, metadataSchemas) && ContainsSchema(operationalSchemas, schema.Trim());
    }

    public static bool IsAllowed(string? schema, IConfiguration? configuration)
        => IsAllowed(
            schema,
            GetOperationalSchemas(configuration),
            [configuration?[MetadataSchemaConfigurationKey]]);

    public static IReadOnlyList<string> GetOperationalSchemas(IConfiguration? configuration)
    {
        var defaultSchema = configuration?[DefaultOperationalSchemaConfigurationKey];
        var schemas = new List<string>
        {
            string.IsNullOrWhiteSpace(defaultSchema) ? DefaultOperationalSchema : defaultSchema.Trim(),
            "public"
        };

        var configured = configuration?.GetSection(OperationalSchemasConfigurationKey).Get<string[]>() ?? [];
        schemas.AddRange(configured.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        return schemas;
    }

    private static bool ContainsSchema(IEnumerable<string?>? schemas, string candidate)
    {
        if (schemas is null)
        {
            return false;
        }

        foreach (var schema in schemas)
        {
            if (!string.IsNullOrWhiteSpace(schema) &&
                string.Equals(candidate, schema.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
