// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static void ObserveHiddenSourceProperties(
        IFeature feature, IDictionary<string, string> warnings, SupportedFileFormat format)
    {
        foreach (var name in feature.Attributes.GetNames())
        {
            var isId = name.Equals("id", StringComparison.OrdinalIgnoreCase);
            if ((!isId || format == SupportedFileFormat.Csv)
                && !name.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var column = isId ? "id" : "geometry";
            warnings.TryAdd(column,
                $"Source property '{name}' conflicts with the importer-owned '{column}' column. "
                + "Its original value is retained in the stored properties document but is hidden in published features. "
                + $"Rename the source property to an unused name such as 'source_{column}' and re-import to publish its values.");
        }
    }

    private static async Task<HashSet<string>> ReadIncompatibleCsvAliasesAsync(
        NpgsqlConnection connection, (string Schema, string Table) target, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND (data_type <> 'text' OR column_name <> lower(column_name))
            """, connection);
        command.Parameters.AddWithValue("schema", target.Schema);
        command.Parameters.AddWithValue("table", target.Table);
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            occupied.Add(reader.GetString(0));
        }
        return occupied;
    }

    private static string? ResolveCsvSourceIdAlias(IReadOnlyList<string> headers, HashSet<string> incompatible)
    {
        if (!headers.Any(name => name.Equals("id", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var occupied = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(incompatible);
        var alias = "source_id";
        for (var suffix = 2; occupied.Contains(alias); suffix++)
        {
            alias = "source_id_" + suffix.ToString(CultureInfo.InvariantCulture);
        }
        return alias;
    }

    private static void PreserveCsvSourceId(IFeature feature, ImportTypedColumnInference inference, string? alias)
    {
        if (alias is null)
        {
            return;
        }
        var sourceName = feature.Attributes.GetNames().FirstOrDefault(name => name.Equals("id", StringComparison.OrdinalIgnoreCase));
        feature.Attributes.Add(alias, sourceName is null ? null : feature.Attributes[sourceName]);
        // Reserve the identity alias before the optional attribute promotion cap.
        inference.ObserveText(alias);
    }

    private static string BuildCsvSourceIdWarning(string alias, IReadOnlyList<ImportTypedColumn> columns)
        => columns.Any(column => column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase))
            ? $"The CSV 'id' column is available as '{alias}'; 'id' is the generated feature key."
            : $"CSV 'id' values were preserved in properties as '{alias}', but the column could not be promoted; 'id' is the generated feature key.";
}
