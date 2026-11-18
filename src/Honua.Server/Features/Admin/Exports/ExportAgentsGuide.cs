// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Exports;

namespace Honua.Server.Features.Admin.Exports;

// AGENTS.md for one export version: what an agent needs to query the GeoParquet file correctly
// without asking: the columns, the coordinate system, and queries that run as written.
// Field names are portable identifiers (ExportRequestRules), so they are safe to quote in SQL.
internal static class ExportAgentsGuide
{
    public static string Render(ExportCompanionInput input)
    {
        var descriptor = input.Descriptor;
        var text = new StringBuilder();
        text.Append("# ").Append(ExportMarkdown.Inline(input.DisplayTitle)).Append(": notes for agents\n\n")
            .Append("Query `").Append(descriptor.Artifacts.Geoparquet.Href)
            .Append("` (GeoParquet 1.1). Paths below are relative to this file; replace them with the file's URL when reading remotely.\n\n");
        AppendCoordinates(text, descriptor);
        AppendFields(text, input.PublicFields, descriptor.Identity.Field);
        AppendQueries(text, descriptor);
        return text.ToString();
    }

    private static void AppendCoordinates(StringBuilder text, ExportVersion descriptor)
        => text.Append("## Coordinate system\n\n")
            .Append("- Geometry column `").Append(descriptor.Geometry.Column).Append("` holds WKB in ")
            .Append(descriptor.Crs.Geoparquet).Append(" (longitude, latitude); the source layer is ")
            .Append(descriptor.Crs.Source).Append(".\n")
            .Append("- Geometry types: ").Append(string.Join(", ", descriptor.Geometry.Types.Select(ExportMarkdown.Inline))).Append(".\n")
            .Append("- Column `bbox` (xmin, ymin, xmax, ymax) is each feature's bounding box; filter on it to skip row groups.\n")
            .Append("- Extent: ").Append(ExportMarkdown.Bounds(descriptor.Bounds)).Append(" (west, south, east, north).\n")
            .Append("- Feature count: ").Append(descriptor.Capture.FeatureCount).Append(".\n\n");

    private static void AppendFields(StringBuilder text, IReadOnlyList<ExportField> fields, string identityField)
    {
        text.Append("## Fields\n\n| Name | Type | Description |\n|---|---|---|\n");
        foreach (var field in fields)
        {
            var notes = new[]
                {
                    field.Title,
                    field.Description,
                    field.Name == identityField ? "Identity: unique per feature, also carried by the vector tiles." : null
                }
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Select(note => ExportMarkdown.Inline(note!));
            text.Append("| `").Append(field.Name).Append("` | ").Append(field.Type).Append(" | ")
                .Append(string.Join("; ", notes)).Append(" |\n");
        }

        text.Append('\n');
    }

    private static void AppendQueries(StringBuilder text, ExportVersion descriptor)
    {
        var file = descriptor.Artifacts.Geoparquet.Href;
        var (west, south, east, north) = (ExportMarkdown.Number(descriptor.Bounds[0]), ExportMarkdown.Number(descriptor.Bounds[1]),
            ExportMarkdown.Number(descriptor.Bounds[2]), ExportMarkdown.Number(descriptor.Bounds[3]));
        text.Append("## Queries\n\nDuckDB:\n\n```sql\nINSTALL spatial; LOAD spatial;\n\n")
            .Append("-- First rows\nSELECT * FROM read_parquet('").Append(file).Append("') LIMIT 10;\n\n")
            .Append("-- One feature by its identity\nSELECT * FROM read_parquet('").Append(file).Append("') WHERE \"")
            .Append(descriptor.Identity.Field).Append("\" = '<value>';\n\n")
            .Append("-- Features in a box (west, south, east, north)\nSELECT * FROM read_parquet('").Append(file).Append("')\n")
            .Append("WHERE bbox.xmin <= ").Append(east).Append(" AND bbox.xmax >= ").Append(west)
            .Append(" AND bbox.ymin <= ").Append(north).Append(" AND bbox.ymax >= ").Append(south).Append(";\n```\n\n")
            .Append("Python:\n\n```python\nimport geopandas as gpd\n\n")
            .Append("frame = gpd.read_parquet(\"").Append(file).Append("\")\n")
            .Append("one = frame[frame[\"").Append(descriptor.Identity.Field).Append("\"] == \"<value>\"]\n```\n");
    }
}
