// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Protocols.Ogc.Api.Records.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Records;

internal static class OgcRecordDescriptive
{
    public static OgcRecordProperties Apply(OgcRecordProperties properties, DescriptiveMetadata metadata) => properties with
    {
        Title = metadata.Title,
        Description = metadata.Description,
        Keywords = DescriptiveMembers.Keywords(metadata.Keywords),
        Themes = DescriptiveMembers.Themes(metadata.Themes),
        Language = DescriptiveMembers.Language(metadata.Language),
        License = metadata.License,
        Rights = metadata.AccessConstraints.Length == 0 ? null : metadata.AccessConstraints,
        Contacts = Contacts(metadata),
    };

    private static ImmutableArray<OgcRecordContact>? Contacts(DescriptiveMetadata metadata)
    {
        var contacts = metadata.Contacts.Select(contact => new OgcRecordContact
        {
            Name = contact.Name ?? (contact.Organization is null ? contact.Email ?? contact.Url : null),
            Organization = contact.Organization,
            Emails = contact.Email is null ? null : [new OgcRecordContactEmail { Value = contact.Email }],
            Links = contact.Url is null ? null : [Link.Create(contact.Url, "about")],
            Roles = [contact.Role],
        }).ToList();
        if (metadata.Publisher is { } publisher && !metadata.Contacts.Any(contact => contact.Role == "publisher"))
        {
            contacts.Add(new OgcRecordContact { Organization = publisher, Roles = ["publisher"] });
        }
        return contacts.Count == 0 ? null : [.. contacts];
    }
}
