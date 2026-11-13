// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    private static readonly XNamespace FeatureSchemaNamespace = "http://www.w3.org/2001/XMLSchema";

    private static string GenerateFeatureSchema(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        HttpContext context,
        string version)
    {
        var groups = featureTypes.GroupBy(descriptor => descriptor.NamespaceUri).ToArray();
        var primary = groups.FirstOrDefault(group => group.Key == FeatureNamespaceUri) ?? groups.FirstOrDefault();
        var profile = ResolveFeatureSchemaProfile(version);
        var schema = CreateFeatureSchemaRoot(primary?.First(), profile);
        foreach (var group in groups.Where(group => group.Key != (primary?.Key ?? FeatureNamespaceUri)))
        {
            schema.Add(CreateFeatureSchemaImport(context, group, version));
        }
        if (primary is not null)
        {
            foreach (var descriptor in primary)
            {
                schema.Add(CreateFeatureTypeDefinitions(descriptor, profile));
            }
        }
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n{schema}\n";
    }

    private static XElement CreateFeatureSchemaRoot(WfsFeatureTypeDescriptor? descriptor, FeatureSchemaProfile profile)
    {
        var prefix = descriptor?.NamespacePrefix ?? FeatureNamespacePrefix;
        var targetNamespace = descriptor?.NamespaceUri ?? FeatureNamespaceUri;
        return new XElement(FeatureSchemaNamespace + "schema",
            new XAttribute(XNamespace.Xmlns + "xsd", FeatureSchemaNamespace),
            new XAttribute(XNamespace.Xmlns + "gml", profile.GmlNamespace),
            new XAttribute(XNamespace.Xmlns + prefix, targetNamespace),
            new XAttribute("targetNamespace", targetNamespace),
            new XAttribute("elementFormDefault", "qualified"),
            new XAttribute("version", "1.0.0"),
            new XElement(FeatureSchemaNamespace + "import",
                new XAttribute("namespace", profile.GmlNamespace),
                new XAttribute("schemaLocation", profile.SchemaLocation)));
    }

    private static XElement CreateFeatureSchemaImport(
        HttpContext context,
        IGrouping<string, WfsFeatureTypeDescriptor> group,
        string version)
    {
        var endpoint = $"{BaseUrlResolver.GetBaseUrl(context).TrimEnd('/')}/wfs";
        var parameterName = version == "2.0.0" ? "typeNames" : "typeName";
        var typeNames = Uri.EscapeDataString(string.Join(",", group.Select(descriptor => descriptor.QualifiedName)));
        var location = BaseUrlResolver.PreserveToken(context.Request,
            $"{endpoint}?service=WFS&version={version}&request=DescribeFeatureType&{parameterName}={typeNames}");
        return new XElement(FeatureSchemaNamespace + "import",
            new XAttribute("namespace", group.Key), new XAttribute("schemaLocation", location));
    }

    private static XElement[] CreateFeatureTypeDefinitions(WfsFeatureTypeDescriptor descriptor, FeatureSchemaProfile profile)
    {
        var properties = new XElement(FeatureSchemaNamespace + "sequence");
        var geometryName = ResolveGeometryPropertyName(descriptor.Resource);
        if (geometryName is not null)
        {
            properties.Add(new XElement(FeatureSchemaNamespace + "element",
                new XAttribute("name", XmlConvert.EncodeLocalName(geometryName)),
                new XAttribute("type", profile.GeometryPropertyType(descriptor.Resource)),
                new XAttribute("minOccurs", "0"), new XAttribute("nillable", "true")));
        }
        properties.Add(GetVisibleAttributeFields(descriptor.Resource).Select(CreateFeatureSchemaField));
        return
        [
            new XElement(FeatureSchemaNamespace + "element",
                new XAttribute("name", descriptor.LocalName),
                new XAttribute("type", $"{descriptor.NamespacePrefix}:{descriptor.LocalName}Type"),
                new XAttribute("substitutionGroup", $"gml:{profile.FeatureElement}")),
            new XElement(FeatureSchemaNamespace + "complexType", new XAttribute("name", $"{descriptor.LocalName}Type"),
                new XElement(FeatureSchemaNamespace + "complexContent",
                    new XElement(FeatureSchemaNamespace + "extension", new XAttribute("base", "gml:AbstractFeatureType"), properties))),
        ];
    }

    private static XElement CreateFeatureSchemaField(MetadataV2Field field)
    {
        var element = new XElement(FeatureSchemaNamespace + "element",
            new XAttribute("name", XmlConvert.EncodeLocalName(field.Name)),
            new XAttribute("type", MapXsdType(field.Type)));
        if (field.Nullable)
        {
            element.Add(new XAttribute("minOccurs", "0"), new XAttribute("nillable", "true"));
        }
        return element;
    }

    private static FeatureSchemaProfile ResolveFeatureSchemaProfile(string version)
        => version switch
        {
            Wfs10Version => new(GmlLegacyNamespace, "http://schemas.opengis.net/gml/2.1.2/feature.xsd", "_Feature"),
            Wfs11Version => new(GmlLegacyNamespace, "http://schemas.opengis.net/gml/3.1.1/base/gml.xsd", "_Feature"),
            _ => new(Wfs20Utilities.GmlNamespace, "http://schemas.opengis.net/gml/3.2.1/gml.xsd", "AbstractFeature"),
        };

    private sealed record FeatureSchemaProfile(string GmlNamespace, string SchemaLocation, string FeatureElement)
    {
        public string GeometryPropertyType(MetadataV2Resource resource)
            => FeatureElement == "AbstractFeature" ? MapGeometryPropertyType(resource.ReadGeometryType()) : "gml:GeometryPropertyType";
    }
}
