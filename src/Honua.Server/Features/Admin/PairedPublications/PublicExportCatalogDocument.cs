// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Publishing.Descriptors;

namespace Honua.Server.Features.Admin.PairedPublications;

// Rebuilds the public root STAC 1.1 catalog.json from the durable list of promoted exports.
// Never edited incrementally so crashes and revocations leave a clean, deterministic root document.
internal static class PublicExportCatalogDocument
{
    internal const string CatalogId = "honua-public-exports";
    internal const string CatalogTitle = "Honua Public Exports";
    internal const string CatalogDescription = "Public cloud-native geospatial exports.";

    public static JsonObject Write(IEnumerable<PromotedExportRecord> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var links = new JsonArray
        {
            CreateSelfLink("self"),
            CreateSelfLink("root")
        };

        foreach (var export in exports.OrderBy(item => item.PublicationId, StringComparer.Ordinal))
        {
            links.Add(CreateChildLink(export));
        }

        return new JsonObject
        {
            ["type"] = "Catalog",
            ["stac_version"] = ExportCollectionDocument.StacVersion,
            ["id"] = CatalogId,
            ["title"] = CatalogTitle,
            ["description"] = CatalogDescription,
            ["links"] = links
        };
    }

    public static byte[] Serialize(IEnumerable<PromotedExportRecord> exports)
        => JsonSerializer.SerializeToUtf8Bytes(Write(exports), PublicationDescriptorJsonContext.Default.JsonObject);

    private static JsonObject CreateSelfLink(string rel) => new()
    {
        ["rel"] = rel,
        ["href"] = "./catalog.json",
        ["type"] = ExportCollectionDocument.MediaType
    };

    private static JsonObject CreateChildLink(PromotedExportRecord export)
    {
        var child = new JsonObject
        {
            ["rel"] = "child",
            ["href"] = $"./publications/{export.PublicationId}/{export.Version}/{ExportCollectionDocument.FileName}",
            ["type"] = ExportCollectionDocument.MediaType
        };
        if (!string.IsNullOrWhiteSpace(export.Title))
        {
            child["title"] = export.Title;
        }

        return child;
    }
}
