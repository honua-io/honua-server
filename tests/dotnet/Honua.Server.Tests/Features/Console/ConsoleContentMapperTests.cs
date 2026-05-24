// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Verifies request → domain projection invariants for Console content items.
/// </summary>
public class ConsoleContentMapperTests
{
    private static ClaimsPrincipal Actor(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], authenticationType: "Test"));

    [UnitTest]
    public void Create_WithExplicitOwnerId_StampsActorAsCreatedBy()
    {
        var request = new CreateConsoleContentItemRequest
        {
            Name = "delegated",
            ItemType = ConsoleContentItemType.Dashboard,
            OwnerId = "target-owner",
        };

        var item = ConsoleContentMapper.FromCreateRequest(request, Actor("acting-admin"));

        Assert.Equal("target-owner", item.OwnerId);
        Assert.Equal("acting-admin", item.CreatedById);
        Assert.Equal("acting-admin", item.UpdatedById);
    }

    [UnitTest]
    public void Create_WithoutOwnerId_DefaultsOwnerToActor()
    {
        var request = new CreateConsoleContentItemRequest
        {
            Name = "self-owned",
            ItemType = ConsoleContentItemType.Layer,
        };

        var item = ConsoleContentMapper.FromCreateRequest(request, Actor("acting-user"));

        Assert.Equal("acting-user", item.OwnerId);
        Assert.Equal("acting-user", item.CreatedById);
        Assert.Equal("acting-user", item.UpdatedById);
    }
}
