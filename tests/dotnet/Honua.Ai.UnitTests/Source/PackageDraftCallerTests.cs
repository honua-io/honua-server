// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// Owner-subject order for package drafts.
public sealed class PackageDraftCallerTests
{
    [UnitTest]
    public void ResolveOwnerId_UsesNameIdentifierThenSubThenApiKeyThenName()
    {
        PackageDraftCaller.ResolveOwnerId(Principal(ClaimTypes.NameIdentifier, "subject-a", "Ada")).Should().Be("subject-a");
        PackageDraftCaller.ResolveOwnerId(Principal("sub", "subject-from-sub", "Ada")).Should().Be("subject-from-sub");
        PackageDraftCaller.ResolveOwnerId(Principal("api_key_id", "key-1", "Ada")).Should().Be("key-1");

        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Ada")],
            authenticationType: "Test"));
        PackageDraftCaller.ResolveOwnerId(nameOnly).Should().Be("Ada");
        PackageDraftCaller.ResolveOwnerId(new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"))).Should().BeNull();
    }

    private static ClaimsPrincipal Principal(string claimType, string subject, string name) =>
        new(new ClaimsIdentity(
            [new Claim(claimType, subject), new Claim(ClaimTypes.Name, name)],
            authenticationType: "Test"));
}
