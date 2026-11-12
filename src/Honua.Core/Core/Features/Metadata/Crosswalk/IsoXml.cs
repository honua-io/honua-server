// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

// Builders shared by the ISO 19139 and ISO 19115-3 encodings, which use the same element names in different namespaces.
internal sealed class IsoXml
{
    public const string LanguageCodeList = "http://www.loc.gov/standards/iso639-2/";

    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";

    public required XNamespace Gco { get; init; }

    public required string CodeLists { get; init; }

    public required XNamespace Identification { get; init; }

    public required XNamespace Extents { get; init; }

    public required XNamespace Constraints { get; init; }

    public XElement Text(string value) => new(Gco + "CharacterString", value);

    public XElement DateTime(DateTimeOffset value)
        => new(Gco + "DateTime", XmlConvert.ToString(value.UtcDateTime, XmlDateTimeSerializationMode.Utc));

    public XElement Code(XName element, string value, string? codeList = null)
        => new(element,
            new XAttribute("codeList", codeList ?? CodeLists + element.LocalName),
            new XAttribute("codeListValue", value),
            value);

    public XElement? Keywords(IReadOnlyList<string> keywords, string? type) =>
        keywords.Count == 0
            ? null
            : new XElement(Identification + "descriptiveKeywords", new XElement(Identification + "MD_Keywords",
                keywords.Select(keyword => new XElement(Identification + "keyword", Text(keyword))),
                type is null ? null : new XElement(Identification + "type", Code(Identification + "MD_KeywordTypeCode", type))));

    public XElement? LegalConstraints(DescriptiveMetadata metadata)
    {
        var statements = new[] { metadata.AccessConstraints, metadata.LicenseUrl ?? metadata.License ?? string.Empty }
            .Where(statement => statement.Length > 0)
            .ToArray();
        if (statements.Length == 0)
        {
            return null;
        }
        var restriction = Code(Constraints + "MD_RestrictionCode", "otherRestrictions");
        return new XElement(Identification + "resourceConstraints", new XElement(Constraints + "MD_LegalConstraints",
            metadata.AccessConstraints.Length == 0 ? null : new XElement(Constraints + "accessConstraints", restriction),
            metadata.License is null ? null : new XElement(Constraints + "useConstraints", new XElement(restriction)),
            statements.Select(statement => new XElement(Constraints + "otherConstraints", Text(statement)))));
    }

    public XElement? Resolution(MetadataV2SpatialResolution? resolution) => resolution switch
    {
        { DistanceMeters: { } meters } => new XElement(Identification + "spatialResolution", new XElement(Identification + "MD_Resolution",
            new XElement(Identification + "distance", new XElement(Gco + "Distance", new XAttribute("uom", "m"), Number(meters))))),
        { ScaleDenominator: { } denominator } => new XElement(Identification + "spatialResolution", new XElement(Identification + "MD_Resolution",
            new XElement(Identification + "equivalentScale", new XElement(Identification + "MD_RepresentativeFraction",
                new XElement(Identification + "denominator", new XElement(Gco + "Integer", denominator)))))),
        _ => null,
    };

    public XElement? Extent(DescriptiveMetadata metadata)
    {
        if (metadata.Extent is null && metadata.TemporalExtent is null)
        {
            return null;
        }
        return new XElement(Identification + "extent", new XElement(Extents + "EX_Extent",
            metadata.Extent is { } box
                ? new XElement(Extents + "geographicElement", new XElement(Extents + "EX_GeographicBoundingBox",
                    Bound("westBoundLongitude", box.West),
                    Bound("eastBoundLongitude", box.East),
                    Bound("southBoundLatitude", box.South),
                    Bound("northBoundLatitude", box.North)))
                : null,
            metadata.TemporalExtent is { } period
                ? new XElement(Extents + "temporalElement", new XElement(Extents + "EX_TemporalExtent",
                    new XElement(Extents + "extent", TimePeriod(metadata.Identifier, period))))
                : null));
    }

    // ISO service metadata needs SV_ServiceIdentification with its operations; services are described in DCAT only.
    public static void RequireDataset(DescriptiveMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Kind != DescribedItemKind.Dataset)
        {
            throw new NotSupportedException($"The ISO encodings describe datasets; '{metadata.Identifier}' is a {metadata.Kind}.");
        }
    }

    private XElement Bound(string name, double value) => new(Extents + name, new XElement(Gco + "Decimal", Number(value)));

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static XElement TimePeriod(string identifier, MetadataV2TimeRange period)
        => new(Gml + "TimePeriod",
            new XAttribute(Gml + "id", "extent-" + XmlConvert.EncodeLocalName(identifier)),
            new XElement(Gml + "beginPosition", Position(period.Start)),
            new XElement(Gml + "endPosition", Position(period.End)));

    private static object Position(DateTimeOffset? value)
        => value is { } instant
            ? XmlConvert.ToString(instant.UtcDateTime, XmlDateTimeSerializationMode.Utc)
            : new XAttribute("indeterminatePosition", "unknown");
}
