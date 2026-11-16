// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneServingProjectionTests
{
    [UnitTheory]
    [InlineData("public")]
    [InlineData("authenticated")]
    [InlineData("private")]
    [InlineData("public-authenticated")]
    public void ToAccessPolicy_AccessFlags_ProtectUnlessOnlyPublic(string access)
    {
        var record = new SceneDatasetRecord
        {
            Id = "scene",
            Name = "Scene",
            AssetRoot = "/data/scene",
            CreatedBy = "tester",
            IsPublic = access is "public" or "public-authenticated",
            RequiresAuth = access is "authenticated" or "public-authenticated",
            AllowedRoles = ["scene-reader"],
        };

        var policy = SceneServingProjection.ToAccessPolicy(record);

        (policy is not null).Should().Be(access != "public");
        policy?.AllowAnonymous.Should().BeFalse();
    }
}
