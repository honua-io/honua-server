// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.TestQuality)]
public sealed class ActorResolverTests
{
    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData(new string?[] { null, null, null }, new string[] { "anonymous", "anonymous", "admin", "anonymous" })]
    [InlineData(new string?[] { "display", "subject", "key" }, new string[] { "display", "subject", "display", "api-key:key" })]
    [InlineData(new string?[] { "display", null, null }, new string[] { "display", "display", "display", "display" })]
    [InlineData(new string?[] { "", null, null }, new string[] { "", "", "admin", "" })]
    [InlineData(new string?[] { " ", null, null }, new string[] { " ", " ", " ", " " })]
    [InlineData(new string?[] { "display", "", "" }, new string[] { "display", "", "display", "display" })]
    [InlineData(new string?[] { "display", " ", " " }, new string[] { "display", " ", "display", "display" })]
    [InlineData(new string?[] { null, "subject", "key" }, new string[] { "anonymous", "subject", "admin", "api-key:key" })]
    public void Resolve_IdentityClaims_PreservesEachPolicy(
        string?[] claimValues, string[] expectedActors)
    {
        var name = claimValues[0];
        var subject = claimValues[1];
        var apiKey = claimValues[2];
        var claims = new List<Claim>();
        if (name != null)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }
        if (subject != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }
        if (apiKey != null)
        {
            claims.Add(new Claim("api_key_id", apiKey));
        }

        foreach (var authenticationType in new string?[] { null, "test" })
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType)),
            };

            Assert.Equal(expectedActors[0], ActorResolver.Resolve(context, ActorResolution.Name));
            Assert.Equal(expectedActors[1], ActorResolver.Resolve(context, ActorResolution.SubjectOrName));
            Assert.Equal(expectedActors[2], ActorResolver.Resolve(context, ActorResolution.AdminName));
            Assert.Equal(expectedActors[3], ActorResolver.Resolve(context, ActorResolution.ApiKeyOrName));
        }
    }
}
