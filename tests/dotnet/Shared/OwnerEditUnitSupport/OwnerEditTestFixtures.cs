// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Tests.OwnerEdit;

internal static class OwnerEditTestFixtures
{
    public static readonly MetadataV2OwnerEditPolicy Policy = new()
    {
        Enabled = true,
        OwnerField = "owner",
        StampOwnerOnInsert = true
    };

    public static MetadataV2Object CitiesResource(MetadataV2OwnerEditPolicy? policy)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "cities", Name = "cities" },
            Type = MetadataV2ObjectType.FeatureDataset,
            OwnerEditPolicy = policy,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }
            ]
        };

    public static DefaultHttpContext Principal(string subject, params string[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }
}
