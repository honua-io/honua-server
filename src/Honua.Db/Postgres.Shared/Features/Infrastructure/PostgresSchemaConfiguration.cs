// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Configuration;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal sealed record PostgresSchemaConfiguration(
    string MetadataSchema,
    string DefaultOperationalSchema,
    IReadOnlyList<string> OperationalSchemas)
{
    public const string DefaultMetadataSchema = "honua";
    public const string DefaultDataSchema = "honua_data";

    public static PostgresSchemaConfiguration FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var metadataSchema = NormalizeSchemaName(configuration["Database:Schema"], DefaultMetadataSchema);
        var defaultOperationalSchema = NormalizeSchemaName(
            configuration["Database:DefaultOperationalSchema"],
            DefaultDataSchema);

        var configuredOperationalSchemas = configuration
            .GetSection("Database:OperationalSchemas")
            .Get<string[]>() ?? [];

        var schemas = new List<string> { defaultOperationalSchema, "public" };
        schemas.AddRange(configuredOperationalSchemas);

        return new PostgresSchemaConfiguration(
            metadataSchema,
            defaultOperationalSchema,
            NormalizeSchemaNames(schemas));
    }

    public IReadOnlyList<string> ResolveDiscoverySchemas(string? currentSchema)
    {
        var schemas = new List<string>(OperationalSchemas);
        AddSchemaIfValid(schemas, currentSchema);

        return NormalizeSchemaNames(schemas)
            .Where(schema => !IsMetadataSchema(schema))
            .ToArray();
    }

    public IReadOnlyList<string> MetadataSchemas
        => NormalizeSchemaNames([MetadataSchema, DefaultMetadataSchema]);

    public bool IsMetadataSchema(string schema)
        => MetadataSchemas.Any(metadataSchema =>
            string.Equals(metadataSchema, schema, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the schema an import writes into: the operational default when none is requested,
    /// otherwise the requested schema, which must be one of the operational schemas
    /// (<see cref="ImportTargetSchemaPolicy"/>). Returns the configured spelling of the schema.
    /// </summary>
    /// <exception cref="ArgumentException">The schema is not a valid identifier or not an operational schema.</exception>
    public string ResolveImportTargetSchema(string? requestedSchema)
    {
        var schema = string.IsNullOrWhiteSpace(requestedSchema)
            ? DefaultOperationalSchema
            : requestedSchema.Trim();

        if (!SchemaSearchPath.IsValidIdentifier(schema))
        {
            throw new ArgumentException("Target schema contains invalid characters.", nameof(requestedSchema));
        }

        return EnsureImportTargetSchemaAllowed(schema, nameof(requestedSchema));
    }

    /// <summary>
    /// Returns the configured spelling of <paramref name="schema"/> when it may be an import target,
    /// and throws when it is not one of the operational schemas or is reserved.
    /// </summary>
    /// <exception cref="ArgumentException">The schema is not an operational schema or is reserved.</exception>
    public string EnsureImportTargetSchemaAllowed(string schema, string parameterName)
    {
        IReadOnlyList<string> operationalSchemas = [DefaultOperationalSchema, .. OperationalSchemas];
        if (!ImportTargetSchemaPolicy.IsAllowed(schema, operationalSchemas, MetadataSchemas))
        {
            throw new ArgumentException(ImportTargetSchemaPolicy.NotOperationalSchemaMessage, parameterName);
        }

        var candidate = schema.Trim();
        return operationalSchemas.First(operational =>
            string.Equals(operational, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSchemaName(string? value, string fallback)
    {
        var schema = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!SchemaSearchPath.IsValidIdentifier(schema))
        {
            throw new InvalidOperationException($"Invalid schema name '{schema}'.");
        }

        return schema;
    }

    private static string[] NormalizeSchemaNames(IEnumerable<string?> schemas)
    {
        var normalized = new List<string>();
        foreach (var schema in schemas)
        {
            AddSchemaIfValid(normalized, schema);
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSchemaIfValid(List<string> schemas, string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        var trimmed = schema.Trim();
        if (!SchemaSearchPath.IsValidIdentifier(trimmed))
        {
            return;
        }

        schemas.Add(trimmed);
    }
}
