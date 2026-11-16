// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

// DCAT 3 JSON-LD using the DCAT-AP 3 controlled vocabularies (EU language, frequency and data-theme tables).
public static class DcatApWriter
{
    private const string EuAuthority = "http://publications.europa.eu/resource/authority/";
    private const string WktLiteral = "http://www.opengis.net/ont/geosparql#wktLiteral";

    public static JsonObject Write(DescriptiveMetadata metadata, string itemUrl)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemUrl);
        var node = new JsonObject
        {
            ["@context"] = Context(),
            ["@id"] = itemUrl,
            ["@type"] = metadata.Kind switch
            {
                DescribedItemKind.Service => "dcat:DataService",
                DescribedItemKind.Catalog => "dcat:Catalog",
                _ => "dcat:Dataset",
            },
            ["dct:identifier"] = metadata.Identifier,
            ["dct:title"] = metadata.Title,
            ["dct:modified"] = Typed(XmlConvert.ToString(metadata.MetadataDate.UtcDateTime, XmlDateTimeSerializationMode.Utc), "xsd:dateTime"),
        };
        Add(node, "dct:description", metadata.Description);
        Add(node, "dcat:keyword", Strings(metadata.Keywords));
        Add(node, "dcat:theme", Themes(metadata.Themes));
        Add(node, "dct:language", MetadataCrosswalk.EuLanguageCode(metadata.Language) is { } language ? Reference(EuAuthority + "language/" + language) : null);
        Add(node, "dct:rights", metadata.AccessConstraints.Length == 0 ? null : Statement("dct:RightsStatement", metadata.AccessConstraints));
        Add(node, "dct:publisher", metadata.Publisher is null ? null : new JsonObject { ["@type"] = "foaf:Agent", ["foaf:name"] = metadata.Publisher });
        Add(node, "dcat:contactPoint", ContactPoints(metadata.Contacts));
        Add(node, "dct:provenance", metadata.Lineage is null ? null : Statement("dct:ProvenanceStatement", metadata.Lineage));
        Add(node, "dct:accrualPeriodicity", MetadataCrosswalk.EuFrequencyCode(metadata.UpdateFrequency) is { } frequency ? Reference(EuAuthority + "frequency/" + frequency) : null);
        Add(node, "dcat:spatialResolutionInMeters", metadata.SpatialResolution?.DistanceMeters is { } meters ? Typed(((decimal)meters).ToString(CultureInfo.InvariantCulture), "xsd:decimal") : null);
        Add(node, "dcat:temporalResolution", metadata.TemporalResolution is { } duration ? Typed(duration, "xsd:duration") : null);
        Add(node, "dct:spatial", Spatial(metadata.Extent));
        Add(node, "dct:temporal", Temporal(metadata.TemporalExtent));
        Add(node, "foaf:page", metadata.MetadataLink("describedby") is { } page ? Reference(page) : null);
        AddAccess(node, metadata);
        return node;
    }

    private static void AddAccess(JsonObject node, DescriptiveMetadata metadata)
    {
        var license = metadata.LicenseUrl is { } url ? Reference(url) : null;
        if (metadata.Kind == DescribedItemKind.Service)
        {
            Add(node, "dcat:endpointURL", metadata.Distributions.Count == 0 ? null : Reference(metadata.Distributions[0].Url));
            Add(node, "dct:license", license);
            return;
        }
        if (metadata.Distributions.Count == 0)
        {
            Add(node, "dct:license", license);
            return;
        }
        node["dcat:distribution"] = new JsonArray(metadata.Distributions.Select(distribution =>
        {
            var item = new JsonObject
            {
                ["@type"] = "dcat:Distribution",
                ["dcat:accessURL"] = Reference(distribution.Url),
            };
            Add(item, "dct:title", distribution.Title);
            Add(item, "dcat:mediaType", distribution.MediaType is null ? null : Reference("https://www.iana.org/assignments/media-types/" + distribution.MediaType));
            Add(item, "dct:license", license?.DeepClone());
            return (JsonNode?)item;
        }).ToArray());
    }

    private static JsonArray? ContactPoints(IReadOnlyList<MetadataV2ResponsibleParty> contacts)
    {
        var points = contacts.Where(contact => contact.Role == MetadataV2DescriptiveCodes.PointOfContactRole)
            .DefaultIfEmpty(contacts.Count > 0 ? contacts[0] : null)
            .OfType<MetadataV2ResponsibleParty>()
            .Select(contact =>
            {
                var card = new JsonObject
                {
                    ["@type"] = contact.Name is null ? "vcard:Organization" : "vcard:Individual",
                    ["vcard:fn"] = contact.Name ?? contact.Organization ?? contact.Email ?? contact.Url,
                };
                Add(card, "vcard:organization-name", contact.Name is null ? null : contact.Organization);
                Add(card, "vcard:hasEmail", contact.Email is null ? null : Reference("mailto:" + contact.Email));
                Add(card, "vcard:hasURL", contact.Url is null ? null : Reference(contact.Url));
                return (JsonNode?)card;
            })
            .ToArray();
        return points.Length == 0 ? null : new JsonArray(points);
    }

    private static JsonArray? Themes(IReadOnlyList<string> themes)
    {
        var references = themes
            .Select(MetadataCrosswalk.ThemeIri)
            .OfType<string>()
            .Select(reference => (JsonNode?)Reference(reference))
            .ToArray();
        return references.Length == 0 ? null : new JsonArray(references);
    }

    private static JsonObject? Spatial(MetadataV2Bbox? box)
    {
        if (box is null)
        {
            return null;
        }
        var w = box.West.ToString("R", CultureInfo.InvariantCulture);
        var e = box.East.ToString("R", CultureInfo.InvariantCulture);
        var s = box.South.ToString("R", CultureInfo.InvariantCulture);
        var n = box.North.ToString("R", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["@type"] = "dct:Location",
            ["dcat:bbox"] = Typed($"POLYGON(({w} {s},{e} {s},{e} {n},{w} {n},{w} {s}))", WktLiteral),
        };
    }

    private static JsonObject? Temporal(MetadataV2TimeRange? period)
    {
        if (period is null || (period.Start is null && period.End is null))
        {
            return null;
        }
        var node = new JsonObject { ["@type"] = "dct:PeriodOfTime" };
        Add(node, "dcat:startDate", period.Start is { } start ? Typed(XmlConvert.ToString(start.UtcDateTime, XmlDateTimeSerializationMode.Utc), "xsd:dateTime") : null);
        Add(node, "dcat:endDate", period.End is { } end ? Typed(XmlConvert.ToString(end.UtcDateTime, XmlDateTimeSerializationMode.Utc), "xsd:dateTime") : null);
        return node;
    }

    private static JsonObject Context() => new()
    {
        ["dcat"] = "http://www.w3.org/ns/dcat#",
        ["dct"] = "http://purl.org/dc/terms/",
        ["foaf"] = "http://xmlns.com/foaf/0.1/",
        ["vcard"] = "http://www.w3.org/2006/vcard/ns#",
        ["rdfs"] = "http://www.w3.org/2000/01/rdf-schema#",
        ["xsd"] = "http://www.w3.org/2001/XMLSchema#",
    };

    private static JsonObject Statement(string type, string label) => new() { ["@type"] = type, ["rdfs:label"] = label };

    private static JsonObject Reference(string iri) => new() { ["@id"] = iri };

    private static JsonObject Typed(string value, string type) => new() { ["@value"] = value, ["@type"] = type };

    private static JsonArray? Strings(IReadOnlyList<string> values)
        => values.Count == 0 ? null : new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static void Add(JsonObject node, string property, JsonNode? value)
    {
        if (value is not null)
        {
            node[property] = value;
        }
    }

    private static void Add(JsonObject node, string property, string? value)
    {
        if (value is not null)
        {
            node[property] = value;
        }
    }
}
