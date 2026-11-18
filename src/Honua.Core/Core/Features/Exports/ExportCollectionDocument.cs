// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Exports;

// The one place that maps an Export version to its STAC 1.1 collection document.
// The document follows the pinned Portolan profile; facts only Honua needs ride in the honua: extension.
public static class ExportCollectionDocument
{
    public const string FileName = "collection.json";
    public const string MediaType = "application/json";
    public const string StacVersion = "1.1.0";
    public const string PortolanSchemaUri = "https://schemas.portolan-sdi.org/portolan/v0.3.0/schema.json";
    public const string FileSchemaUri = "https://stac-extensions.github.io/file/v2.1.0/schema.json";
    public const string TableSchemaUri = "https://stac-extensions.github.io/table/v1.2.0/schema.json";
    public const string VersionSchemaUri = "https://stac-extensions.github.io/version/v1.2.0/schema.json";
    public const string HonuaSchemaUri = "https://honua.io/schemas/stac/honua/v1.0.0/schema.json";
    public const string HonuaExtensionVersion = "1.0.0";
    public const string DataAssetKey = "geoparquet";
    public const string VisualAssetKey = "pmtiles";

    // A multihash prefix: SHA2-256 (0x12) with a 32-byte digest (0x20).
    private const string Sha256Multihash = "1220";

    public static JsonObject Write(ExportVersion export, ExportCollectionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(metadata);
        var publicFields = export.Fields.Where(field => field.Public).ToList();
        var title = export.Title ?? export.PublicationId;
        var collection = new JsonObject
        {
            ["type"] = "Collection",
            ["stac_version"] = StacVersion,
            ["stac_extensions"] = Strings([PortolanSchemaUri, FileSchemaUri, TableSchemaUri, VersionSchemaUri, HonuaSchemaUri]),
            ["id"] = export.PublicationId,
            ["title"] = title,
            ["description"] = metadata.Description ?? $"{title}, exported as PMTiles and GeoParquet files.",
            ["license"] = License(metadata.License),
            ["version"] = export.Version,
            ["updated"] = Timestamp(export.PublishedAt),
            ["extent"] = Extent(export),
            ["table:columns"] = new JsonArray([.. publicFields.Select(Column)])
        };
        AddHonuaFacts(collection, export, publicFields);
        collection["links"] = new JsonArray(new JsonObject
        {
            ["rel"] = "pmtiles",
            ["href"] = export.Artifacts.Pmtiles.Href,
            ["type"] = PmtilesExportArtifact.MediaTypeValue,
            ["pmtiles:layers"] = Strings([export.Artifacts.Pmtiles.VectorLayer])
        });
        collection["assets"] = new JsonObject
        {
            [DataAssetKey] = DataAsset(export.Artifacts.Geoparquet),
            [VisualAssetKey] = VisualAsset(export.Artifacts.Pmtiles)
        };
        return collection;
    }

    // STAC wants one SPDX identifier; anything else, including free text and "proprietary", is "other".
    private static string License(string? license)
        => SpdxLicensePolicy.IsLicenseIdentifier(license) ? license! : "other";

    private static void AddHonuaFacts(JsonObject collection, ExportVersion export, List<ExportVersionField> publicFields)
    {
        collection["honua:capture_id"] = export.Capture.CaptureId;
        collection["honua:captured_at"] = Timestamp(export.Capture.CapturedAt);
        collection["honua:source_revision"] = export.Capture.SourceRevision;
        collection["honua:feature_count"] = export.Capture.FeatureCount;
        collection["honua:source"] = Source(export);
        collection["honua:source_crs"] = export.Crs.Source;
        collection["honua:identity"] = new JsonObject
        {
            ["field"] = export.Identity.Field,
            ["pmtiles_property"] = export.Identity.PmtilesProperty,
            ["geoparquet_column"] = export.Identity.GeoparquetColumn
        };
        collection["honua:geometry"] = Geometry(export.Geometry);
        collection["honua:public_fields"] = Strings(publicFields.Select(field => field.Name));
        collection["honua:validation"] = Validation(export.Validation);
    }

    // Reads a document this class wrote. Returns null when a required Honua fact is missing or malformed,
    // so a hand-edited or third-party collection is never mistaken for an Export version.
    public static ExportVersion? Read(JsonObject collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        try
        {
            return ReadRequired(collection);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static ExportVersion ReadRequired(JsonObject collection)
    {
        var assets = Node(collection, "assets").AsObject();
        var data = Node(assets, DataAssetKey).AsObject();
        var visual = Node(assets, VisualAssetKey).AsObject();
        var source = Node(collection, "honua:source").AsObject();
        var identity = Node(collection, "honua:identity").AsObject();
        var bbox = Node(Node(Node(collection, "extent").AsObject(), "spatial").AsObject(), "bbox").AsArray()[0]?.AsArray()
            ?? throw new FormatException("extent.spatial.bbox is empty.");
        if (bbox.Count != 4)
        {
            throw new FormatException("extent.spatial.bbox is not [west, south, east, north].");
        }

        var title = Node(collection, "title").GetValue<string>();
        var id = Text(collection, "id");
        return new ExportVersion
        {
            PublicationId = id,
            Version = Text(collection, "version"),
            Title = title == id ? null : title,
            PublishedAt = Instant(collection, "updated"),
            SourceLayer = new ExportSourceLayer(Text(source, "service"), Text(source, "layer")) { Title = OptionalText(source, "title") },
            Capture = new ExportCapture(Text(collection, "honua:capture_id"), Instant(collection, "honua:captured_at"),
                Text(collection, "honua:source_revision"), Node(collection, "honua:feature_count").GetValue<long>()),
            Bounds = [.. bbox.Select(value => Item(value).GetValue<double>())],
            Crs = new ExportCrs(Text(collection, "honua:source_crs"), "OGC:CRS84"),
            Fields = ReadColumns(collection),
            Identity = new ExportIdentity(Text(identity, "field"), Text(identity, "pmtiles_property"), Text(identity, "geoparquet_column")),
            Geometry = ReadGeometry(Node(collection, "honua:geometry").AsObject()),
            Artifacts = new ExportArtifacts(ReadVisual(collection, visual), ReadData(data)),
            Validation = ReadValidation(Node(collection, "honua:validation").AsObject())
        };
    }

    private static JsonObject Extent(ExportVersion export) => new()
    {
        ["spatial"] = new JsonObject { ["bbox"] = new JsonArray(new JsonArray([.. export.Bounds.Select(value => (JsonNode)value)])) },
        ["temporal"] = new JsonObject
        {
            ["interval"] = new JsonArray(Strings([Timestamp(export.Capture.CapturedAt), Timestamp(export.Capture.CapturedAt)]))
        }
    };

    private static JsonObject Source(ExportVersion export)
    {
        var source = new JsonObject { ["service"] = export.SourceLayer.Service, ["layer"] = export.SourceLayer.Layer };
        AddOptional(source, "title", export.SourceLayer.Title);
        return source;
    }

    private static JsonObject Column(ExportVersionField field)
    {
        var column = new JsonObject { ["name"] = field.Name, ["type"] = field.Type };
        AddOptional(column, "title", field.Title);
        AddOptional(column, "description", field.Description);
        return column;
    }

    private static ExportVersionField[] ReadColumns(JsonObject collection)
    {
        var publicFields = ReadStrings(Node(collection, "honua:public_fields")).ToHashSet(StringComparer.Ordinal);
        return [.. Node(collection, "table:columns").AsArray().Select(column => ReadColumn(Item(column).AsObject(), publicFields))];
    }

    private static ExportVersionField ReadColumn(JsonObject column, HashSet<string> publicFields)
    {
        var name = Text(column, "name");
        return new(name, Text(column, "type"), publicFields.Contains(name))
        {
            Title = OptionalText(column, "title"),
            Description = OptionalText(column, "description")
        };
    }

    private static JsonObject Geometry(ExportGeometry geometry)
    {
        var node = new JsonObject
        {
            ["column"] = geometry.Column,
            ["encoding"] = geometry.Encoding,
            ["types"] = Strings(geometry.Types)
        };
        AddOptional(node, "bbox_column", geometry.BboxColumn);
        return node;
    }

    private static ExportGeometry ReadGeometry(JsonObject geometry)
        => new(Text(geometry, "column"), Text(geometry, "encoding"), ReadStrings(Node(geometry, "types")))
        {
            BboxColumn = OptionalText(geometry, "bbox_column")
        };

    private static JsonObject Validation(ExportValidation validation) => new()
    {
        ["status"] = validation.Status,
        ["validated_at"] = Timestamp(validation.ValidatedAt),
        ["checks"] = new JsonArray([.. validation.Checks.Select(check =>
        {
            var node = new JsonObject { ["name"] = check.Name, ["status"] = check.Status };
            AddOptional(node, "detail", check.Detail);
            return (JsonNode)node;
        })])
    };

    private static ExportValidation ReadValidation(JsonObject validation)
        => new(Text(validation, "status"), Instant(validation, "validated_at"),
            [.. Node(validation, "checks").AsArray().Select(item => ReadCheck(Item(item).AsObject()))]);

    private static ExportValidationCheck ReadCheck(JsonObject check)
        => new(Text(check, "name"), Text(check, "status")) { Detail = OptionalText(check, "detail") };

    private static JsonObject DataAsset(GeoparquetExportArtifact artifact) => new()
    {
        ["href"] = artifact.Href,
        ["type"] = GeoparquetExportArtifact.MediaTypeValue,
        ["roles"] = Strings(["data"]),
        ["title"] = "GeoParquet",
        ["file:size"] = artifact.Bytes,
        ["file:checksum"] = Sha256Multihash + artifact.Sha256,
        ["honua:capture_id"] = artifact.CaptureId,
        ["honua:fields"] = Strings(artifact.Fields)
    };

    private static GeoparquetExportArtifact ReadData(JsonObject asset) => new()
    {
        Href = Text(asset, "href"),
        Bytes = Node(asset, "file:size").GetValue<long>(),
        Sha256 = Sha256(asset),
        CaptureId = Text(asset, "honua:capture_id"),
        Fields = ReadStrings(Node(asset, "honua:fields"))
    };

    private static JsonObject VisualAsset(PmtilesExportArtifact artifact) => new()
    {
        ["href"] = artifact.Href,
        ["type"] = PmtilesExportArtifact.MediaTypeValue,
        ["roles"] = Strings(["visual"]),
        ["title"] = "PMTiles",
        ["file:size"] = artifact.Bytes,
        ["file:checksum"] = Sha256Multihash + artifact.Sha256,
        ["honua:capture_id"] = artifact.CaptureId,
        ["honua:fields"] = Strings(artifact.Fields),
        ["honua:minzoom"] = artifact.Minzoom,
        ["honua:maxzoom"] = artifact.Maxzoom
    };

    private static PmtilesExportArtifact ReadVisual(JsonObject collection, JsonObject asset)
    {
        var link = Node(collection, "links").AsArray().Select(node => Item(node).AsObject())
            .First(node => OptionalText(node, "rel") == "pmtiles");
        return new PmtilesExportArtifact
        {
            Href = Text(asset, "href"),
            Bytes = Node(asset, "file:size").GetValue<long>(),
            Sha256 = Sha256(asset),
            CaptureId = Text(asset, "honua:capture_id"),
            Fields = ReadStrings(Node(asset, "honua:fields")),
            VectorLayer = ReadStrings(Node(link, "pmtiles:layers")).Single(),
            Minzoom = Node(asset, "honua:minzoom").GetValue<int>(),
            Maxzoom = Node(asset, "honua:maxzoom").GetValue<int>()
        };
    }

    private static string Sha256(JsonObject asset)
    {
        var checksum = Text(asset, "file:checksum");
        if (!checksum.StartsWith(Sha256Multihash, StringComparison.Ordinal) || checksum.Length != Sha256Multihash.Length + 64
            || !checksum.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
        {
            throw new FormatException("file:checksum is not a SHA2-256 multihash.");
        }

        return checksum[Sha256Multihash.Length..];
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);

    private static string[] ReadStrings(JsonNode array) => [.. array.AsArray().Select(value => Item(value).GetValue<string>())];

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonObject node, string property)
        => DateTimeOffset.Parse(Text(node, property), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static JsonNode Node(JsonObject node, string property)
        => node[property] ?? throw new FormatException($"The collection has no {property}.");

    private static JsonNode Item(JsonNode? item) => item ?? throw new FormatException("The collection holds a null array item.");

    private static string Text(JsonObject node, string property) => Node(node, property).GetValue<string>();

    private static string? OptionalText(JsonObject node, string property) => node[property]?.GetValue<string>();

    private static void AddOptional(JsonObject node, string property, string? value)
    {
        if (value is not null)
        {
            node[property] = value;
        }
    }
}

// Dataset metadata the collection carries beyond the Export version itself.
public sealed record ExportCollectionMetadata(string? Description, string? License)
{
    public static readonly ExportCollectionMetadata None = new(null, null);
}
