// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

// ISO 19139:2007 encoding of ISO 19115:2003, element order per the gmd schemas.
public static class Iso19139Writer
{
    private static readonly XNamespace Gmd = "http://www.isotc211.org/2005/gmd";
    private static readonly XNamespace Gco = "http://www.isotc211.org/2005/gco";
    private const string CodeLists = "http://standards.iso.org/iso/19139/resources/gmxCodelists.xml#";

    public static XDocument Write(DescriptiveMetadata metadata)
    {
        IsoXml.RequireDataset(metadata);
        var iso = new IsoXml { Gco = Gco, CodeLists = CodeLists, Identification = Gmd, Extents = Gmd, Constraints = Gmd };
        const string scope = "dataset";
        return new XDocument(new XElement(Gmd + "MD_Metadata",
            new XAttribute(XNamespace.Xmlns + "gmd", Gmd.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "gco", Gco.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "gml", "http://www.opengis.net/gml/3.2"),
            new XElement(Gmd + "fileIdentifier", iso.Text(metadata.Identifier)),
            new XElement(Gmd + "language", Language(iso, metadata.Language)),
            new XElement(Gmd + "characterSet", iso.Code(Gmd + "MD_CharacterSetCode", "utf8")),
            new XElement(Gmd + "hierarchyLevel", iso.Code(Gmd + "MD_ScopeCode", scope)),
            Responsible(iso, Gmd + "contact", MetadataContact(metadata)),
            new XElement(Gmd + "dateStamp", iso.DateTime(metadata.MetadataDate)),
            new XElement(Gmd + "metadataStandardName", iso.Text("ISO 19115:2003/19139")),
            new XElement(Gmd + "metadataStandardVersion", iso.Text("1.0")),
            metadata.Crs.Select(crs => new XElement(Gmd + "referenceSystemInfo",
                new XElement(Gmd + "MD_ReferenceSystem", new XElement(Gmd + "referenceSystemIdentifier",
                    new XElement(Gmd + "RS_Identifier", new XElement(Gmd + "code", iso.Text(crs))))))),
            new XElement(Gmd + "identificationInfo", Identification(iso, metadata)),
            Distribution(iso, metadata),
            Lineage(iso, metadata, scope)));
    }

    private static XElement Identification(IsoXml iso, DescriptiveMetadata metadata) =>
        new(Gmd + "MD_DataIdentification",
            new XElement(Gmd + "citation", new XElement(Gmd + "CI_Citation",
                new XElement(Gmd + "title", iso.Text(metadata.Title)),
                new XElement(Gmd + "date", new XElement(Gmd + "CI_Date",
                    new XElement(Gmd + "date", iso.DateTime(metadata.MetadataDate)),
                    new XElement(Gmd + "dateType", iso.Code(Gmd + "CI_DateTypeCode", "revision")))))),
            new XElement(Gmd + "abstract", iso.Text(metadata.Description ?? metadata.Title)),
            metadata.Attribution is { } credit ? new XElement(Gmd + "credit", iso.Text(credit)) : null,
            Iso19139Contacts(metadata).Select(contact => Responsible(iso, Gmd + "pointOfContact", contact)),
            metadata.UpdateFrequency is { } frequency && MetadataV2DescriptiveCodes.Iso19139MaintenanceFrequencies.Contains(frequency)
                ? new XElement(Gmd + "resourceMaintenance", new XElement(Gmd + "MD_MaintenanceInformation",
                    new XElement(Gmd + "maintenanceAndUpdateFrequency", iso.Code(Gmd + "MD_MaintenanceFrequencyCode", frequency))))
                : null,
            iso.Keywords(metadata.Keywords, type: null),
            iso.Keywords(metadata.Themes, type: "theme"),
            iso.LegalConstraints(metadata),
            iso.Resolution(metadata.SpatialResolution),
            new XElement(Gmd + "language", Language(iso, metadata.Language)),
            new XElement(Gmd + "characterSet", iso.Code(Gmd + "MD_CharacterSetCode", "utf8")),
            metadata.TopicCategories.Where(MetadataV2DescriptiveCodes.Iso19139TopicCategories.Contains)
                .Select(category => new XElement(Gmd + "topicCategory", new XElement(Gmd + "MD_TopicCategoryCode", category))),
            iso.Extent(metadata));

    private static XElement? Distribution(IsoXml iso, DescriptiveMetadata metadata) =>
        metadata.Distributions.Count == 0
            ? null
            : new XElement(Gmd + "distributionInfo", new XElement(Gmd + "MD_Distribution",
                new XElement(Gmd + "transferOptions", new XElement(Gmd + "MD_DigitalTransferOptions",
                    metadata.Distributions.Select(distribution => new XElement(Gmd + "onLine", new XElement(Gmd + "CI_OnlineResource",
                        new XElement(Gmd + "linkage", new XElement(Gmd + "URL", distribution.Url)),
                        new XElement(Gmd + "protocol", iso.Text(distribution.Protocol)),
                        distribution.Title is null ? null : new XElement(Gmd + "name", iso.Text(distribution.Title)))))))));

    private static XElement? Lineage(IsoXml iso, DescriptiveMetadata metadata, string scope) =>
        metadata.Lineage is null
            ? null
            : new XElement(Gmd + "dataQualityInfo", new XElement(Gmd + "DQ_DataQuality",
                new XElement(Gmd + "scope", new XElement(Gmd + "DQ_Scope", new XElement(Gmd + "level", iso.Code(Gmd + "MD_ScopeCode", scope)))),
                new XElement(Gmd + "lineage", new XElement(Gmd + "LI_Lineage", new XElement(Gmd + "statement", iso.Text(metadata.Lineage))))));

    private static XElement Responsible(IsoXml iso, XName property, MetadataV2ResponsibleParty party)
    {
        var email = party.Email is null ? null : new XElement(Gmd + "address", new XElement(Gmd + "CI_Address",
            new XElement(Gmd + "electronicMailAddress", iso.Text(party.Email))));
        var online = party.Url is null ? null : new XElement(Gmd + "onlineResource", new XElement(Gmd + "CI_OnlineResource",
            new XElement(Gmd + "linkage", new XElement(Gmd + "URL", party.Url))));
        return new XElement(property, new XElement(Gmd + "CI_ResponsibleParty",
            party.Name is null ? null : new XElement(Gmd + "individualName", iso.Text(party.Name)),
            party.Organization is null ? null : new XElement(Gmd + "organisationName", iso.Text(party.Organization)),
            email is null && online is null ? null : new XElement(Gmd + "contactInfo", new XElement(Gmd + "CI_Contact", email, online)),
            new XElement(Gmd + "role", iso.Code(Gmd + "CI_RoleCode", party.Role))));
    }

    private static IEnumerable<MetadataV2ResponsibleParty> Iso19139Contacts(DescriptiveMetadata metadata)
    {
        var contacts = metadata.Contacts.Where(contact => MetadataV2DescriptiveCodes.Iso19139Roles.Contains(contact.Role));
        if (metadata.Publisher is not null && !metadata.Contacts.Any(contact => contact.Role == MetadataV2DescriptiveCodes.PublisherRole))
        {
            return contacts.Append(new MetadataV2ResponsibleParty
            {
                Role = MetadataV2DescriptiveCodes.PublisherRole,
                Organization = metadata.Publisher,
            });
        }
        return contacts;
    }

    private static MetadataV2ResponsibleParty MetadataContact(DescriptiveMetadata metadata)
    {
        var contact = metadata.PointOfContact;
        return contact is not null && MetadataV2DescriptiveCodes.Iso19139Roles.Contains(contact.Role)
            ? contact
            : new MetadataV2ResponsibleParty
            {
                Role = MetadataV2DescriptiveCodes.PointOfContactRole,
                Organization = metadata.Publisher ?? contact?.Organization,
                Name = contact?.Name,
                Email = contact?.Email,
                Url = contact?.Url,
            };
    }

    private static XElement Language(IsoXml iso, string? language)
        => iso.Code(Gmd + "LanguageCode", MetadataCrosswalk.IsoLanguage(language), IsoXml.LanguageCodeList);
}
