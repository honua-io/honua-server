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

    private static ClaimsPrincipal AdminApiKeyActor(string apiKeyId, string apiKeyName) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "admin-api-key"),
                new Claim("api_key_id", apiKeyId),
                new Claim("api_key_name", apiKeyName),
            },
            authenticationType: "Test"));

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

    [UnitTest]
    public void Create_WithAdminApiKeyPrincipal_StampsApiKeyIdAsAuditActor()
    {
        // Admin API-key principals carry api_key_id / api_key_name / ClaimTypes.Name
        // but no NameIdentifier/sub. The mapper must still stamp a non-empty
        // audit actor so createdById/updatedById are populated.
        var request = new CreateConsoleContentItemRequest
        {
            Name = "api-key-created",
            ItemType = ConsoleContentItemType.Layer,
        };

        var item = ConsoleContentMapper.FromCreateRequest(
            request,
            AdminApiKeyActor("11111111-2222-3333-4444-555555555555", "Console CI key"));

        Assert.False(string.IsNullOrEmpty(item.CreatedById));
        Assert.False(string.IsNullOrEmpty(item.UpdatedById));
        Assert.Equal("11111111-2222-3333-4444-555555555555", item.CreatedById);
        Assert.Equal("11111111-2222-3333-4444-555555555555", item.UpdatedById);
        Assert.Equal(item.CreatedById, item.OwnerId);
    }
}
