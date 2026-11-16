// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Crosswalk;

// ISO 19115-3:2018 XML encoding of ISO 19115-1, element order per the mdb 2.0 schemas.
public static class Iso19115Part3Writer
{
    private static readonly XNamespace Mdb = "http://standards.iso.org/iso/19115/-3/mdb/2.0";
    private static readonly XNamespace Cit = "http://standards.iso.org/iso/19115/-3/cit/2.0";
    private static readonly XNamespace Mri = "http://standards.iso.org/iso/19115/-3/mri/1.0";
    private static readonly XNamespace Mcc = "http://standards.iso.org/iso/19115/-3/mcc/1.0";
    private static readonly XNamespace Lan = "http://standards.iso.org/iso/19115/-3/lan/1.0";
    private static readonly XNamespace Gco = "http://standards.iso.org/iso/19115/-3/gco/1.0";
    private static readonly XNamespace Gex = "http://standards.iso.org/iso/19115/-3/gex/1.0";
    private static readonly XNamespace Mco = "http://standards.iso.org/iso/19115/-3/mco/1.0";
    private static readonly XNamespace Mmi = "http://standards.iso.org/iso/19115/-3/mmi/1.0";
    private static readonly XNamespace Mrd = "http://standards.iso.org/iso/19115/-3/mrd/1.0";
    private static readonly XNamespace Mrl = "http://standards.iso.org/iso/19115/-3/mrl/2.0";
    private static readonly XNamespace Mrs = "http://standards.iso.org/iso/19115/-3/mrs/1.0";
    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";
    private const string CodeLists = "https://schemas.isotc211.org/19115/resources/Codelists/cat/codelists.xml#";
    private const string Scope = "dataset";

    public static XDocument Write(DescriptiveMetadata metadata)
    {
        IsoXml.RequireDataset(metadata);
        var iso = new IsoXml { Gco = Gco, CodeLists = CodeLists, Identification = Mri, Extents = Gex, Constraints = Mco };
        return new XDocument(new XElement(Mdb + "MD_Metadata",
            new[] { Mdb, Cit, Mri, Mcc, Lan, Gco, Gex, Mco, Mmi, Mrd, Mrl, Mrs, Gml }
                .Select(ns => new XAttribute(XNamespace.Xmlns + Prefix(ns), ns.NamespaceName)),
            new XElement(Mdb + "metadataIdentifier", new XElement(Mcc + "MD_Identifier", new XElement(Mcc + "code", iso.Text(metadata.Identifier)))),
            new XElement(Mdb + "defaultLocale", Locale(iso, metadata.Language)),
            new XElement(Mdb + "metadataScope", new XElement(Mdb + "MD_MetadataScope",
                new XElement(Mdb + "resourceScope", iso.Code(Mcc + "MD_ScopeCode", Scope)))),
            Responsibility(iso, Mdb + "contact", metadata.PointOfContact ?? PublisherParty(metadata)),
            new XElement(Mdb + "dateInfo", Date(iso, metadata.MetadataDate)),
            new XElement(Mdb + "metadataStandard", new XElement(Cit + "CI_Citation",
                new XElement(Cit + "title", iso.Text("ISO 19115-3")),
                new XElement(Cit + "edition", iso.Text("2018")))),
            metadata.Crs.Select(crs => new XElement(Mdb + "referenceSystemInfo", new XElement(Mrs + "MD_ReferenceSystem",
                new XElement(Mrs + "referenceSystemIdentifier", new XElement(Mcc + "MD_Identifier", new XElement(Mcc + "code", iso.Text(crs))))))),
            new XElement(Mdb + "identificationInfo", Identification(iso, metadata)),
            Distribution(iso, metadata),
            metadata.Lineage is null ? null : new XElement(Mdb + "resourceLineage", new XElement(Mrl + "LI_Lineage",
                new XElement(Mrl + "statement", iso.Text(metadata.Lineage)),
                new XElement(Mrl + "scope", new XElement(Mcc + "MD_Scope", new XElement(Mcc + "level", iso.Code(Mcc + "MD_ScopeCode", Scope))))))));
    }

    private static XElement Identification(IsoXml iso, DescriptiveMetadata metadata) =>
        new(Mri + "MD_DataIdentification",
            new XElement(Mri + "citation", new XElement(Cit + "CI_Citation",
                new XElement(Cit + "title", iso.Text(metadata.Title)),
                new XElement(Cit + "date", Date(iso, metadata.MetadataDate)))),
            new XElement(Mri + "abstract", iso.Text(metadata.Description ?? metadata.Title)),
            metadata.Attribution is { } credit ? new XElement(Mri + "credit", iso.Text(credit)) : null,
            Parties(metadata).Select(party => Responsibility(iso, Mri + "pointOfContact", party)),
            iso.Resolution(metadata.SpatialResolution),
            metadata.TemporalResolution is { } duration
                ? new XElement(Mri + "temporalResolution", new XElement(Gco + "TM_PeriodDuration", duration))
                : null,
            metadata.TopicCategories.Select(category => new XElement(Mri + "topicCategory", new XElement(Mri + "MD_TopicCategoryCode", category))),
            iso.Extent(metadata),
            metadata.UpdateFrequency is { } frequency
                ? new XElement(Mri + "resourceMaintenance", new XElement(Mmi + "MD_MaintenanceInformation",
                    new XElement(Mmi + "maintenanceAndUpdateFrequency", iso.Code(Mmi + "MD_MaintenanceFrequencyCode", frequency))))
                : null,
            iso.Keywords(metadata.Keywords, type: null),
            iso.Keywords(metadata.Themes, type: "theme"),
            iso.LegalConstraints(metadata),
            new XElement(Mri + "defaultLocale", Locale(iso, metadata.Language)));

    private static IEnumerable<MetadataV2ResponsibleParty> Parties(DescriptiveMetadata metadata)
    {
        var publisherListed = metadata.Contacts.Any(contact => contact.Role == MetadataV2DescriptiveCodes.PublisherRole);
        return publisherListed || metadata.Publisher is null ? metadata.Contacts : [.. metadata.Contacts, PublisherParty(metadata)];
    }

    private static MetadataV2ResponsibleParty PublisherParty(DescriptiveMetadata metadata)
        => new() { Role = MetadataV2DescriptiveCodes.PublisherRole, Organization = metadata.Publisher ?? metadata.Title };

    private static XElement? Distribution(IsoXml iso, DescriptiveMetadata metadata) =>
        metadata.Distributions.Count == 0
            ? null
            : new XElement(Mdb + "distributionInfo", new XElement(Mrd + "MD_Distribution",
                new XElement(Mrd + "transferOptions", new XElement(Mrd + "MD_DigitalTransferOptions",
                    metadata.Distributions.Select(distribution => new XElement(Mrd + "onLine", OnlineResource(iso, new(distribution.Url, distribution.Protocol, distribution.Title))))))));

    private static XElement Responsibility(IsoXml iso, XName property, MetadataV2ResponsibleParty party)
    {
        var contact = party.Email is null && party.Url is null
            ? null
            : new XElement(Cit + "contactInfo", new XElement(Cit + "CI_Contact",
                party.Email is null ? null : new XElement(Cit + "address", new XElement(Cit + "CI_Address",
                    new XElement(Cit + "electronicMailAddress", iso.Text(party.Email)))),
                party.Url is null ? null : new XElement(Cit + "onlineResource", OnlineResource(iso, new(party.Url)))));
        var individual = party.Name is null ? null : new XElement(Cit + "CI_Individual", new XElement(Cit + "name", iso.Text(party.Name)));
        var partyElement = party.Organization is null && individual is not null
            ? new XElement(Cit + "CI_Individual", individual.Elements(), contact)
            : new XElement(Cit + "CI_Organisation",
                new XElement(Cit + "name", iso.Text(party.Organization ?? string.Empty)),
                contact,
                individual is null ? null : new XElement(Cit + "individual", individual));
        return new XElement(property, new XElement(Cit + "CI_Responsibility",
            new XElement(Cit + "role", iso.Code(Cit + "CI_RoleCode", party.Role)),
            new XElement(Cit + "party", partyElement)));
    }

    private sealed record OnlineResourceDetails(string Url, string? Protocol = null, string? Name = null);

    private static XElement OnlineResource(IsoXml iso, OnlineResourceDetails resource)
        => new(Cit + "CI_OnlineResource",
            new XElement(Cit + "linkage", iso.Text(resource.Url)),
            resource.Protocol is null ? null : new XElement(Cit + "protocol", iso.Text(resource.Protocol)),
            resource.Name is null ? null : new XElement(Cit + "name", iso.Text(resource.Name)));

    private static XElement Date(IsoXml iso, DateTimeOffset date)
        => new(Cit + "CI_Date",
            new XElement(Cit + "date", iso.DateTime(date)),
            new XElement(Cit + "dateType", iso.Code(Cit + "CI_DateTypeCode", "revision")));

    private static XElement Locale(IsoXml iso, string? language)
        => new(Lan + "PT_Locale",
            new XElement(Lan + "language", iso.Code(Lan + "LanguageCode", MetadataCrosswalk.IsoLanguage(language), IsoXml.LanguageCodeList)),
            new XElement(Lan + "characterEncoding", iso.Code(Lan + "MD_CharacterSetCode", "utf8")));

    private static string Prefix(XNamespace ns) => ns == Gml ? "gml" : ns.NamespaceName.Split('/')[^2];
}
