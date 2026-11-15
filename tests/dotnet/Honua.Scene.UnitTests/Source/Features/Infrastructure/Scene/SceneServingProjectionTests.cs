// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneServingProjectionTests
{
    [UnitTheory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void ToAccessPolicy_AccessFlags_ProtectUnlessOnlyPublic(bool isPublic, bool requiresAuth, bool expectProtected)
    {
        var record = new SceneDatasetRecord
        {
            Id = "scene",
            Name = "Scene",
            AssetRoot = "/data/scene",
            CreatedBy = "tester",
            IsPublic = isPublic,
            RequiresAuth = requiresAuth,
            AllowedRoles = ["scene-reader"],
        };

        var policy = SceneServingProjection.ToAccessPolicy(record);

        (policy is not null).Should().Be(expectProtected);
        policy?.AllowAnonymous.Should().BeFalse();
    }
}
