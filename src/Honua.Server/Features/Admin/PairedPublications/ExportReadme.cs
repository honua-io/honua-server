// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Server.Features.Admin.PairedPublications;

// README.md for one export version: what the data is, under which licence, where it came from,
// when it was captured, and which file to open for what.
internal static class ExportReadme
{
    public static string Render(ExportCompanionInput input)
    {
        var descriptor = input.Descriptor;
        var text = new StringBuilder();
        text.Append("# ").Append(ExportMarkdown.Inline(input.DisplayTitle)).Append("\n\n");
        if (input.Description is { } description)
        {
            text.Append(ExportMarkdown.Block(description)).Append("\n\n");
        }

        text.Append("| | |\n|---|---|\n");
        foreach (var (label, value) in Facts(input))
        {
            text.Append("| ").Append(label).Append(" | ").Append(value).Append(" |\n");
        }

        text.Append("\n## Files\n\n")
            .Append("- `").Append(descriptor.Artifacts.Geoparquet.Href)
            .Append("`: GeoParquet with every feature and its public fields. Open it with DuckDB, GeoPandas, QGIS or any GeoParquet reader.\n")
            .Append("- `").Append(descriptor.Artifacts.Pmtiles.Href)
            .Append("`: PMTiles vector tiles for web maps (tile layer `").Append(descriptor.Artifacts.Pmtiles.VectorLayer).Append("`).\n")
            .Append("- `").Append(ExportCompanionFiles.StyleFileName).Append("`: a MapLibre style that draws the tiles.\n")
            .Append("- `").Append(ExportCompanionFiles.AgentsFileName).Append("`: the fields, the coordinate system and ready-to-run queries.\n");
        return text.ToString();
    }

    private static IEnumerable<(string Label, string Value)> Facts(ExportCompanionInput input)
    {
        var descriptor = input.Descriptor;
        var metadata = input.Metadata;
        yield return ("Licence", ExportMarkdown.Inline(ExportStacFacts.LicenseLabel(metadata)));
        if (!string.IsNullOrWhiteSpace(metadata.Attribution))
        {
            yield return ("Attribution", ExportMarkdown.Inline(metadata.Attribution));
        }

        yield return ("Source", ExportMarkdown.Inline(Source(input)));
        yield return ("Updated", descriptor.Capture.CapturedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        yield return ("Version", ExportMarkdown.Inline(descriptor.Version));
        yield return ("Features", descriptor.Capture.FeatureCount.ToString("N0", CultureInfo.InvariantCulture));
        yield return ("Extent (WGS 84)", ExportMarkdown.Bounds(descriptor.Bounds));
    }

    private static string Source(ExportCompanionInput input)
    {
        var layer = input.Descriptor.SourceLayer;
        var source = $"Layer {layer.Layer} of service {layer.Service}, hosted by {input.HostName}";
        return string.IsNullOrWhiteSpace(input.Metadata.Publisher) ? source : $"{source}; published by {input.Metadata.Publisher.Trim()}";
    }
}
