// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class CatalogAudienceEndpointsTests
{
    private static MetadataV2Graph Graph()
    {
        var identities = new[] { "public", "staff", "secret" };
        return new()
        {
            Revision = 1,
            Catalogs = [Catalog("public", "public"), Catalog("staff", "signed-in"), Catalog("responders", "roles")],
            Services = identities.Select(id => new MetadataV2Service
            {
                Metadata = new() { Id = id, Name = id, Title = id, Publisher = id + "-owner", Tags = [id + "-tag"] },
                Protocols = [ServiceProtocols.FeatureServer],
                AccessPolicy = id == "public" ? new() { AllowAnonymous = true }
                    : id == "staff" ? new() : new() { AllowedRoles = ["responder"] }
            }).ToArray(),
            Resources = identities.Select(id => new MetadataV2Resource
            {
                Metadata = new() { Id = id + "-dataset", Name = id, Title = id, Publisher = id + "-owner", Tags = [id + "-tag"] },
                AccessPolicy = new() { AllowAnonymous = true }
            }).ToArray(),
            Publications = identities.Select(id => new MetadataV2Publication
            {
                Metadata = new() { Id = id + "-layer", Name = id },
                ResourceId = id + "-dataset", ServiceId = id, LayerIndex = 0, IsPrimary = true
            }).ToArray()
        };
    }

    private static MetadataV2Catalog Catalog(string id, string audience)
    {
        var json = $$$"""
            {"metadata":{"id":"{{{id}}}","name":"{{{id}}}","title":"{{{id}}}"},"target":"discovery","status":{},
             "configuration":{"audience":{"kind":"{{{audience}}}","roleIds":["responder"]},
             "scope":{"folderIds":[],"tags":[],"kinds":["service","layer","dataset","map","app"]},
             "defaultListing":"automatic"}}
            """;
        return JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Catalog)!;
    }
}
