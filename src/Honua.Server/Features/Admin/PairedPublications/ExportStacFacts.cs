// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.PairedPublications;

// Licence, providers and table:columns for the export's STAC document, from metadata Honua holds.
internal static class ExportStacFacts
{
    internal const string TableExtension = "https://stac-extensions.github.io/table/v1.2.0/schema.json";
    private const string Proprietary = "proprietary";

    // Metadata licences are canonical SPDX or the literal 'proprietary' (MetadataV2GraphValidation);
    // STAC 1.1 spells anything that is not an SPDX expression as "other".
    public static string? License(MetadataV2ObjectMetadata metadata)
        => string.IsNullOrWhiteSpace(metadata.License) ? null
            : string.Equals(metadata.License, Proprietary, StringComparison.OrdinalIgnoreCase) ? "other"
            : metadata.License.Trim();

    public static string LicenseLabel(MetadataV2ObjectMetadata metadata)
        => License(metadata) switch
        {
            null => "Not stated",
            "other" => "Proprietary",
            var spdx => spdx
        };

    public static JsonArray Providers(ExportCompanionInput input)
    {
        var providers = new JsonArray();
        if (!string.IsNullOrWhiteSpace(input.Metadata.Publisher))
        {
            var producer = new JsonObject { ["name"] = input.Metadata.Publisher.Trim(), ["roles"] = new JsonArray("producer") };
            if (WebUrl(input.Metadata.ContactPoint?.Url) is { } url)
            {
                producer["url"] = url;
            }

            providers.Add(producer);
        }

        providers.Add(new JsonObject { ["name"] = input.HostName, ["roles"] = new JsonArray("host") });
        return providers;
    }

    // The GeoParquet columns: the public fields, then the geometry and its bounding box.
    public static JsonArray TableColumns(ExportCompanionInput input)
    {
        var columns = new JsonArray();
        foreach (var field in input.PublicFields)
        {
            columns.Add(Column(field.Name, field.Type, field.Description ?? field.Title));
        }

        var descriptor = input.Descriptor;
        columns.Add(Column(descriptor.Geometry.Column, "geometry", $"Feature geometry as WKB in {descriptor.Crs.Geoparquet}."));
        columns.Add(Column("bbox", "struct", "Feature bounding box (xmin, ymin, xmax, ymax) for spatial filtering."));
        return columns;
    }

    // Adds the facts to a STAC object's properties and declares the table extension on the object.
    public static void Apply(JsonObject stac, JsonObject properties, ExportCompanionInput input)
    {
        if (License(input.Metadata) is { } license)
        {
            properties["license"] = license;
        }

        properties["providers"] = Providers(input);
        properties["table:columns"] = TableColumns(input);
        stac["stac_extensions"] = new JsonArray(TableExtension);
    }

    private static JsonObject Column(string name, string type, string? description)
    {
        var column = new JsonObject { ["name"] = name, ["type"] = type };
        if (!string.IsNullOrWhiteSpace(description))
        {
            column["description"] = description.Trim();
        }

        return column;
    }

    private static string? WebUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
}
