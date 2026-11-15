// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Protocols.Scene;
using Honua.Protocols.Scene.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

public sealed class SceneDiscoveryAuthProjectionTests
{
    [UnitTest]
    public void ToAuth_RoleRestrictedRecord_AdvertisesAuthenticationWithoutRoleNames()
    {
        var auth = SceneDiscoveryEndpoints.ToAuth(CreateRecord(isPublic: false, requiresAuth: true, "scene-reader"));
        var json = JsonSerializer.Serialize(auth, PublicSceneDiscoveryJsonContext.Default.PublicSceneAuthRequirements);

        auth.RequiresAuthentication.Should().BeTrue();
        auth.Schemes.Should().NotBeEmpty();
        json.Should().NotContain("scene-reader");
    }

    [UnitTest]
    public void ToAuth_PublicRecord_RequiresNoAuthentication()
    {
        var auth = SceneDiscoveryEndpoints.ToAuth(CreateRecord(isPublic: true, requiresAuth: false));

        auth.RequiresAuthentication.Should().BeFalse();
        auth.Schemes.Should().BeEmpty();
    }

    private static SceneDatasetRecord CreateRecord(bool isPublic, bool requiresAuth, params string[] allowedRoles)
        => new()
        {
            Id = "scene",
            Name = "Scene",
            AssetRoot = "/data/scene",
            CreatedBy = "tester",
            IsPublic = isPublic,
            RequiresAuth = requiresAuth,
            AllowedRoles = allowedRoles,
        };
}
