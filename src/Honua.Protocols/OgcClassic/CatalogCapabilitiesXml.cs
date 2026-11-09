// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Classic;

internal static class CatalogCapabilitiesXml
{
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    public static XElement Provider(MetadataV2ObjectMetadata metadata, XNamespace ows)
    {
        var contact = metadata.ContactPoint;
        return new XElement(ows + "ServiceProvider",
            new XElement(ows + "ProviderName", metadata.Publisher ?? string.Empty),
            string.IsNullOrWhiteSpace(contact?.Url) ? null : new XElement(ows + "ProviderSite", new XAttribute(Xlink + "href", contact.Url)),
            new XElement(ows + "ServiceContact",
                string.IsNullOrWhiteSpace(contact?.Name) ? null : new XElement(ows + "IndividualName", contact.Name),
                contact is null ? null : new XElement(ows + "ContactInfo",
                    string.IsNullOrWhiteSpace(contact.Email) ? null : new XElement(ows + "Address",
                        new XElement(ows + "ElectronicMailAddress", contact.Email)),
                    string.IsNullOrWhiteSpace(contact.Url) ? null : new XElement(ows + "OnlineResource", new XAttribute(Xlink + "href", contact.Url)))));
    }

    public static XElement? WmsContact(MetadataV2ObjectMetadata metadata)
    {
        var contact = metadata.ContactPoint;
        if (contact is null && metadata.Publisher is null)
            return null;
        return new XElement("ContactInformation",
            new XElement("ContactPersonPrimary",
                new XElement("ContactPerson", contact?.Name ?? string.Empty),
                new XElement("ContactOrganization", metadata.Publisher ?? string.Empty)),
            string.IsNullOrWhiteSpace(contact?.Email) ? null : new XElement("ContactElectronicMailAddress", contact.Email));
    }

    public static IEnumerable<XElement> Keywords(MetadataV2ObjectMetadata metadata, XNamespace ows) =>
        metadata.Keywords.Count == 0 ? [] : [new XElement(ows + "Keywords", metadata.Keywords.Select(keyword => new XElement(ows + "Keyword", keyword)))];
}
