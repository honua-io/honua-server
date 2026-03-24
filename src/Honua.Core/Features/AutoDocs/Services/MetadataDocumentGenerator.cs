// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Xml;
using Honua.Core.Features.AutoDocs.Abstractions;
using Honua.Core.Features.AutoDocs.Domain;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.AutoDocs.Services;

/// <summary>
/// Deterministic, template-driven metadata document generator.
/// Produces ISO 19115 XML, FGDC XML, and Markdown data dictionaries
/// from layer schema and metadata without LLM dependency.
/// </summary>
public sealed class MetadataDocumentGenerator : IMetadataDocumentGenerator
{
    private const string Iso19115Namespace = "http://www.isotc211.org/2005/gmd";
    private const string GcoNamespace = "http://www.isotc211.org/2005/gco";

    /// <inheritdoc />
    public MetadataDocumentResult Generate(MetadataDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MetadataDocumentResult
        {
            Iso19115Xml = GenerateIso19115(request),
            FgdcXml = GenerateFgdc(request),
            DataDictionary = GenerateDataDictionary(request),
        };
    }

    private static string GenerateIso19115(MetadataDocumentRequest request)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("gmd", "MD_Metadata", Iso19115Namespace);
        writer.WriteAttributeString("xmlns", "gco", null, GcoNamespace);

        // File identifier
        WriteCharacterString(writer, "gmd", "fileIdentifier", Iso19115Namespace,
            $"{request.ServiceName}.{request.Layer.Name}");

        // Language
        WriteCharacterString(writer, "gmd", "language", Iso19115Namespace, "eng");

        // Hierarchy level
        writer.WriteStartElement("gmd", "hierarchyLevel", Iso19115Namespace);
        writer.WriteStartElement("gmd", "MD_ScopeCode", Iso19115Namespace);
        writer.WriteAttributeString("codeList",
            "http://standards.iso.org/iso/19115/resources/Codelists/cat/codelists.xml#MD_ScopeCode");
        writer.WriteAttributeString("codeListValue", "dataset");
        writer.WriteString("dataset");
        writer.WriteEndElement(); // MD_ScopeCode
        writer.WriteEndElement(); // hierarchyLevel

        // Contact
        if (request.OrganizationName is not null || request.ContactEmail is not null)
        {
            writer.WriteStartElement("gmd", "contact", Iso19115Namespace);
            writer.WriteStartElement("gmd", "CI_ResponsibleParty", Iso19115Namespace);
            if (request.OrganizationName is not null)
            {
                WriteCharacterString(writer, "gmd", "organisationName", Iso19115Namespace,
                    request.OrganizationName);
            }
            if (request.ContactEmail is not null)
            {
                writer.WriteStartElement("gmd", "contactInfo", Iso19115Namespace);
                writer.WriteStartElement("gmd", "CI_Contact", Iso19115Namespace);
                writer.WriteStartElement("gmd", "address", Iso19115Namespace);
                writer.WriteStartElement("gmd", "CI_Address", Iso19115Namespace);
                WriteCharacterString(writer, "gmd", "electronicMailAddress", Iso19115Namespace,
                    request.ContactEmail);
                writer.WriteEndElement(); // CI_Address
                writer.WriteEndElement(); // address
                writer.WriteEndElement(); // CI_Contact
                writer.WriteEndElement(); // contactInfo
            }
            writer.WriteStartElement("gmd", "role", Iso19115Namespace);
            writer.WriteStartElement("gmd", "CI_RoleCode", Iso19115Namespace);
            writer.WriteAttributeString("codeList",
                "http://standards.iso.org/iso/19115/resources/Codelists/cat/codelists.xml#CI_RoleCode");
            writer.WriteAttributeString("codeListValue", "pointOfContact");
            writer.WriteString("pointOfContact");
            writer.WriteEndElement(); // CI_RoleCode
            writer.WriteEndElement(); // role
            writer.WriteEndElement(); // CI_ResponsibleParty
            writer.WriteEndElement(); // contact
        }

        // Date stamp
        writer.WriteStartElement("gmd", "dateStamp", Iso19115Namespace);
        writer.WriteStartElement("gco", "Date", GcoNamespace);
        writer.WriteString(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteEndElement(); // Date
        writer.WriteEndElement(); // dateStamp

        // Reference system
        writer.WriteStartElement("gmd", "referenceSystemInfo", Iso19115Namespace);
        writer.WriteStartElement("gmd", "MD_ReferenceSystem", Iso19115Namespace);
        writer.WriteStartElement("gmd", "referenceSystemIdentifier", Iso19115Namespace);
        writer.WriteStartElement("gmd", "RS_Identifier", Iso19115Namespace);
        WriteCharacterString(writer, "gmd", "code", Iso19115Namespace,
            $"EPSG:{request.Layer.SpatialReference.Wkid}");
        writer.WriteEndElement(); // RS_Identifier
        writer.WriteEndElement(); // referenceSystemIdentifier
        writer.WriteEndElement(); // MD_ReferenceSystem
        writer.WriteEndElement(); // referenceSystemInfo

        // Identification info
        writer.WriteStartElement("gmd", "identificationInfo", Iso19115Namespace);
        writer.WriteStartElement("gmd", "MD_DataIdentification", Iso19115Namespace);

        // Citation
        writer.WriteStartElement("gmd", "citation", Iso19115Namespace);
        writer.WriteStartElement("gmd", "CI_Citation", Iso19115Namespace);
        WriteCharacterString(writer, "gmd", "title", Iso19115Namespace, request.Layer.Name);
        writer.WriteStartElement("gmd", "date", Iso19115Namespace);
        writer.WriteStartElement("gmd", "CI_Date", Iso19115Namespace);
        writer.WriteStartElement("gmd", "date", Iso19115Namespace);
        writer.WriteStartElement("gco", "Date", GcoNamespace);
        writer.WriteString(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteEndElement(); // Date
        writer.WriteEndElement(); // date
        writer.WriteStartElement("gmd", "dateType", Iso19115Namespace);
        writer.WriteStartElement("gmd", "CI_DateTypeCode", Iso19115Namespace);
        writer.WriteAttributeString("codeList",
            "http://standards.iso.org/iso/19115/resources/Codelists/cat/codelists.xml#CI_DateTypeCode");
        writer.WriteAttributeString("codeListValue", "creation");
        writer.WriteString("creation");
        writer.WriteEndElement(); // CI_DateTypeCode
        writer.WriteEndElement(); // dateType
        writer.WriteEndElement(); // CI_Date
        writer.WriteEndElement(); // date
        writer.WriteEndElement(); // CI_Citation
        writer.WriteEndElement(); // citation

        // Abstract
        WriteCharacterString(writer, "gmd", "abstract", Iso19115Namespace,
            request.Abstract ?? request.Layer.Description ?? $"Dataset: {request.Layer.Name}");

        // Purpose
        if (request.Purpose is not null)
        {
            WriteCharacterString(writer, "gmd", "purpose", Iso19115Namespace, request.Purpose);
        }

        // Keywords
        if (request.Keywords is { Count: > 0 })
        {
            writer.WriteStartElement("gmd", "descriptiveKeywords", Iso19115Namespace);
            writer.WriteStartElement("gmd", "MD_Keywords", Iso19115Namespace);
            foreach (var keyword in request.Keywords)
            {
                WriteCharacterString(writer, "gmd", "keyword", Iso19115Namespace, keyword);
            }
            writer.WriteEndElement(); // MD_Keywords
            writer.WriteEndElement(); // descriptiveKeywords
        }

        // Constraints
        if (request.AccessConstraints is not null)
        {
            writer.WriteStartElement("gmd", "resourceConstraints", Iso19115Namespace);
            writer.WriteStartElement("gmd", "MD_LegalConstraints", Iso19115Namespace);
            WriteCharacterString(writer, "gmd", "useLimitation", Iso19115Namespace,
                request.AccessConstraints);
            writer.WriteEndElement(); // MD_LegalConstraints
            writer.WriteEndElement(); // resourceConstraints
        }

        // Spatial representation type
        writer.WriteStartElement("gmd", "spatialRepresentationType", Iso19115Namespace);
        writer.WriteStartElement("gmd", "MD_SpatialRepresentationTypeCode", Iso19115Namespace);
        writer.WriteAttributeString("codeList",
            "http://standards.iso.org/iso/19115/resources/Codelists/cat/codelists.xml#MD_SpatialRepresentationTypeCode");
        writer.WriteAttributeString("codeListValue", "vector");
        writer.WriteString("vector");
        writer.WriteEndElement(); // MD_SpatialRepresentationTypeCode
        writer.WriteEndElement(); // spatialRepresentationType

        // Language
        WriteCharacterString(writer, "gmd", "language", Iso19115Namespace, "eng");

        // Extent
        if (request.Layer.Extent is not null)
        {
            writer.WriteStartElement("gmd", "extent", Iso19115Namespace);
            writer.WriteStartElement("gmd", "EX_Extent", Iso19115Namespace);
            writer.WriteStartElement("gmd", "geographicElement", Iso19115Namespace);
            writer.WriteStartElement("gmd", "EX_GeographicBoundingBox", Iso19115Namespace);
            WriteDecimalElement(writer, "westBoundLongitude", request.Layer.Extent.Value.MinX);
            WriteDecimalElement(writer, "eastBoundLongitude", request.Layer.Extent.Value.MaxX);
            WriteDecimalElement(writer, "southBoundLatitude", request.Layer.Extent.Value.MinY);
            WriteDecimalElement(writer, "northBoundLatitude", request.Layer.Extent.Value.MaxY);
            writer.WriteEndElement(); // EX_GeographicBoundingBox
            writer.WriteEndElement(); // geographicElement
            writer.WriteEndElement(); // EX_Extent
            writer.WriteEndElement(); // extent
        }

        writer.WriteEndElement(); // MD_DataIdentification
        writer.WriteEndElement(); // identificationInfo

        writer.WriteEndElement(); // MD_Metadata
        writer.WriteEndDocument();
        writer.Flush();

        return sb.ToString();
    }

    private static string GenerateFgdc(MetadataDocumentRequest request)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("metadata");

        // Identification information
        writer.WriteStartElement("idinfo");

        // Citation
        writer.WriteStartElement("citation");
        writer.WriteStartElement("citeinfo");
        writer.WriteElementString("origin", request.OrganizationName ?? "Unknown");
        writer.WriteElementString("pubdate",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        writer.WriteElementString("title", request.Layer.Name);
        writer.WriteEndElement(); // citeinfo
        writer.WriteEndElement(); // citation

        // Description
        writer.WriteStartElement("descript");
        writer.WriteElementString("abstract",
            request.Abstract ?? request.Layer.Description ?? $"Dataset: {request.Layer.Name}");
        writer.WriteElementString("purpose", request.Purpose ?? "Not specified");
        writer.WriteEndElement(); // descript

        // Status
        writer.WriteStartElement("status");
        writer.WriteElementString("progress", "Complete");
        writer.WriteElementString("update", request.UpdateFrequency ?? "As needed");
        writer.WriteEndElement(); // status

        // Spatial domain
        if (request.Layer.Extent is not null)
        {
            writer.WriteStartElement("spdom");
            writer.WriteStartElement("bounding");
            writer.WriteElementString("westbc",
                request.Layer.Extent.Value.MinX.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("eastbc",
                request.Layer.Extent.Value.MaxX.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("northbc",
                request.Layer.Extent.Value.MaxY.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("southbc",
                request.Layer.Extent.Value.MinY.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement(); // bounding
            writer.WriteEndElement(); // spdom
        }

        // Keywords
        if (request.Keywords is { Count: > 0 })
        {
            writer.WriteStartElement("keywords");
            writer.WriteStartElement("theme");
            writer.WriteElementString("themekt", "None");
            foreach (var kw in request.Keywords)
            {
                writer.WriteElementString("themekey", kw);
            }
            writer.WriteEndElement(); // theme
            writer.WriteEndElement(); // keywords
        }

        // Access constraints
        writer.WriteElementString("accconst", request.AccessConstraints ?? "None");
        writer.WriteElementString("useconst", "None");

        writer.WriteEndElement(); // idinfo

        // Spatial reference
        writer.WriteStartElement("spref");
        writer.WriteStartElement("horizsys");
        writer.WriteStartElement("cordsysn");
        writer.WriteElementString("geogcsn", $"EPSG:{request.Layer.SpatialReference.Wkid}");
        writer.WriteEndElement(); // cordsysn
        writer.WriteEndElement(); // horizsys
        writer.WriteEndElement(); // spref

        // Entity and attribute info
        writer.WriteStartElement("eainfo");
        writer.WriteStartElement("detailed");
        writer.WriteStartElement("enttyp");
        writer.WriteElementString("enttypl", request.Layer.Name);
        writer.WriteElementString("enttypd",
            request.Layer.Description ?? $"Feature class: {request.Layer.Name}");
        writer.WriteElementString("enttypds", request.OrganizationName ?? "Unknown");
        writer.WriteEndElement(); // enttyp

        foreach (var field in request.Layer.Fields)
        {
            if (field.IsGeometry) continue;
            writer.WriteStartElement("attr");
            writer.WriteElementString("attrlabl", field.Name);
            writer.WriteElementString("attrdef", field.Description ?? field.Name);
            writer.WriteElementString("attrdefs", request.OrganizationName ?? "Unknown");
            writer.WriteStartElement("attrdomv");
            writer.WriteElementString("udom",
                $"Type: {field.Type}" + (field.Length.HasValue ? $", MaxLength: {field.Length}" : ""));
            writer.WriteEndElement(); // attrdomv
            writer.WriteEndElement(); // attr
        }

        writer.WriteEndElement(); // detailed
        writer.WriteEndElement(); // eainfo

        // Metadata reference
        writer.WriteStartElement("metainfo");
        writer.WriteElementString("metd",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        writer.WriteElementString("metstdn", "FGDC Content Standard for Digital Geospatial Metadata");
        writer.WriteElementString("metstdv", "FGDC-STD-001-1998");
        writer.WriteEndElement(); // metainfo

        writer.WriteEndElement(); // metadata
        writer.WriteEndDocument();
        writer.Flush();

        return sb.ToString();
    }

    private static string GenerateDataDictionary(MetadataDocumentRequest request)
    {
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"# Data Dictionary: {request.Layer.Name}").AppendLine();
        sb.AppendLine();

        if (request.Layer.Description is not null)
        {
            sb.AppendLine(request.Layer.Description);
            sb.AppendLine();
        }

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- **Service**: {request.ServiceName}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- **Geometry Type**: {request.Layer.GeometryType}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- **Spatial Reference**: EPSG:{request.Layer.SpatialReference.Wkid} ({request.Layer.SpatialReference.DisplayName})").AppendLine();

        if (request.OrganizationName is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **Organization**: {request.OrganizationName}").AppendLine();
        }

        if (request.ContactEmail is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **Contact**: {request.ContactEmail}").AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("## Fields");
        sb.AppendLine();
        sb.AppendLine("| Field Name | Type | Length | Nullable | Description |");
        sb.AppendLine("|-----------|------|--------|----------|-------------|");

        foreach (var field in request.Layer.Fields)
        {
            if (field.IsGeometry) continue;
            var lengthStr = field.Length.HasValue ? field.Length.Value.ToString(CultureInfo.InvariantCulture) : "-";
            var nullableStr = field.Nullable ? "Yes" : "No";
            var desc = field.Description ?? "-";
            sb.Append(CultureInfo.InvariantCulture, $"| {field.Name} | {field.Type} | {lengthStr} | {nullableStr} | {desc} |").AppendLine();
        }

        if (request.Layer.HasGeometry && request.Layer.GeometryField is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Geometry");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"- **Column**: {request.Layer.GeometryField.Name}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"- **Type**: {request.Layer.GeometryType}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"- **SRID**: {request.Layer.SpatialReference.Wkid}").AppendLine();
        }

        if (request.Keywords is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Keywords");
            sb.AppendLine();
            sb.AppendLine(string.Join(", ", request.Keywords));
        }

        if (request.AccessConstraints is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Access Constraints");
            sb.AppendLine();
            sb.AppendLine(request.AccessConstraints);
        }

        return sb.ToString();
    }

    private static void WriteCharacterString(XmlWriter writer, string prefix,
        string localName, string ns, string value)
    {
        writer.WriteStartElement(prefix, localName, ns);
        writer.WriteStartElement("gco", "CharacterString", GcoNamespace);
        writer.WriteString(value);
        writer.WriteEndElement(); // CharacterString
        writer.WriteEndElement();
    }

    private static void WriteDecimalElement(XmlWriter writer, string localName, double value)
    {
        writer.WriteStartElement("gmd", localName, Iso19115Namespace);
        writer.WriteStartElement("gco", "Decimal", GcoNamespace);
        writer.WriteString(value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement(); // Decimal
        writer.WriteEndElement();
    }
}
