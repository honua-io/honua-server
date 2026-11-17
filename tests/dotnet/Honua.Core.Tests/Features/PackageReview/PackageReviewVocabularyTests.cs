// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Tests.Features.PackageReview;

public sealed class PackageReviewVocabularyTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("map", true)]
    [InlineData("app", true)]
    [InlineData("map_package", false)]
    [InlineData("app_package", false)]
    public void ReviewFamilies_UseCanonicalNames(string family, bool expected)
        => PackageReviewFamilies.IsKnown(family).Should().Be(expected);

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("honua_create_map", "create_map")]
    [InlineData("honua_create_app", "create_app")]
    [InlineData("honua_validate_package", "validate_content")]
    [InlineData("honua_preview_package", "preview_content")]
    public void CapabilityTools_UseCanonicalNames(string tool, string standardName)
    {
        var descriptor = new CapabilityRegistry().Find(CapabilityRegistry.McpToolIdPrefix + tool["honua_".Length..]);
        descriptor.Should().NotBeNull();
        descriptor!.StandardName.Should().Be(standardName);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("maps", "mapId")]
    [InlineData("apps", "appId")]
    public void CapabilityResources_UseCanonicalNames(string family, string id)
    {
        var descriptor = new CapabilityRegistry().Find(CapabilityRegistry.McpResourceIdPrefix + family);
        descriptor.Should().NotBeNull();
        descriptor!.ResourceUriTemplates.Should().ContainSingle().Which.Should().Be($"honua://{family}/{{{id}}}");
    }
}
