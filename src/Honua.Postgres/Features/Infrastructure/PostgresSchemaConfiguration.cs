// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Postgres.Features.Infrastructure;

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
