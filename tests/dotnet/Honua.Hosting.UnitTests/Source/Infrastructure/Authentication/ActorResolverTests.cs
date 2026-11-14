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
    [InlineData(null, null, null, "anonymous", "anonymous", "admin", "anonymous")]
    [InlineData("display", "subject", "key", "display", "subject", "display", "api-key:key")]
    [InlineData("display", null, null, "display", "display", "display", "display")]
    [InlineData("", null, null, "", "", "admin", "")]
    [InlineData(" ", null, null, " ", " ", " ", " ")]
    [InlineData("display", "", "", "display", "", "display", "display")]
    [InlineData("display", " ", " ", "display", " ", "display", "display")]
    [InlineData(null, "subject", "key", "anonymous", "subject", "admin", "api-key:key")]
    public void Resolve_IdentityClaims_PreservesEachPolicy(
        string? name, string? subject, string? apiKey,
        string expectedName, string expectedSubject, string expectedAdmin, string expectedApiKey)
    {
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

            Assert.Equal(expectedName, ActorResolver.Resolve(context, ActorResolution.Name));
            Assert.Equal(expectedSubject, ActorResolver.Resolve(context, ActorResolution.SubjectOrName));
            Assert.Equal(expectedAdmin, ActorResolver.Resolve(context, ActorResolution.AdminName));
            Assert.Equal(expectedApiKey, ActorResolver.Resolve(context, ActorResolution.ApiKeyOrName));
        }
    }
}
