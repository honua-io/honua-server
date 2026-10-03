// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Target-schema rule shared by every import path that creates, replaces or writes rows into a
/// table (SEC-23). A requested target must be one of the configured operational schemas: the
/// default operational schema, <c>public</c>, and <c>Database:OperationalSchemas</c>. The server's
/// metadata schema and PostgreSQL's system schemas are refused even when listed there.
/// </summary>
public static class ImportTargetSchemaPolicy
{
    /// <summary>The built-in metadata schema, reserved even when another metadata schema is configured.</summary>
    public const string DefaultMetadataSchema = "honua";

    /// <summary>The operational schema used when <see cref="DefaultOperationalSchemaConfigurationKey"/> is unset.</summary>
    public const string DefaultOperationalSchema = "honua_data";

    /// <summary>Configuration key that names the metadata schema.</summary>
    public const string MetadataSchemaConfigurationKey = "Database:Schema";

    /// <summary>Configuration key that names the default operational schema.</summary>
    public const string DefaultOperationalSchemaConfigurationKey = "Database:DefaultOperationalSchema";

    /// <summary>Configuration section listing additional operational schemas.</summary>
    public const string OperationalSchemasConfigurationKey = "Database:OperationalSchemas";

    /// <summary>Client-safe refusal message for a target outside the configured operational schemas.</summary>
    public const string NotOperationalSchemaMessage =
        "Target schema is not a configured operational schema. Import targets are limited to the default operational schema, public, and Database:OperationalSchemas.";

    /// <summary>Client-safe refusal message for the metadata schema or a database system schema.</summary>
    public const string ReservedSchemaMessage =
        "Schema is reserved for server metadata or database system catalogs and cannot be used by an import.";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="schema"/> is the server metadata schema
    /// (the built-in one or any of <paramref name="metadataSchemas"/>) or a PostgreSQL system
    /// schema. An omitted schema is not reserved: it resolves to the operational default downstream.
    /// </summary>
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

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="schema"/> may be an import target: it is
    /// omitted (the operational default applies), or it is one of <paramref name="operationalSchemas"/>
    /// and not reserved by <see cref="IsReserved(string?, IEnumerable{string?}?)"/>.
    /// </summary>
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

    /// <summary>
    /// Evaluates <paramref name="schema"/> against the operational and metadata schemas named in
    /// <paramref name="configuration"/>, with the same defaults the PostgreSQL provider applies.
    /// </summary>
    public static bool IsAllowed(string? schema, IConfiguration? configuration)
        => IsAllowed(
            schema,
            GetOperationalSchemas(configuration),
            [configuration?[MetadataSchemaConfigurationKey]]);

    /// <summary>
    /// The configured operational schemas: the default operational schema, <c>public</c>, and
    /// <see cref="OperationalSchemasConfigurationKey"/>.
    /// </summary>
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
