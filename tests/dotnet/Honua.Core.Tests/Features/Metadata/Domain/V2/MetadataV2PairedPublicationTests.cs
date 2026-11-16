// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2PairedPublicationTests
{
    [UnitTest]
    public void CanonicalGraph_RejectsSharedArtifactNamespace()
    {
        var recipe = new MetadataV2PairedPublication { PublicationId = "published-roads", IdentityField = "name" };
        var graph = new MetadataV2Graph
        {
            Publications =
            [
                new() { Metadata = new() { Id = "first" }, ResourceId = "roads", ServiceId = "public", PairedPublication = recipe },
                new() { Metadata = new() { Id = "second" }, ResourceId = "private-roads", ServiceId = "private", PairedPublication = recipe }
            ]
        };

        MetadataV2GraphValidator.Validate(graph).Errors.Should().Contain(
            "paired publication id 'published-roads' is assigned to more than one publication.");
    }

    [UnitTheory]
    [InlineData("../invalid", "name", 0, 8, 10000)]
    [InlineData("published-roads", "hidden", 0, 8, 10000)]
    [InlineData("published-roads", "name", 9, 8, 10000)]
    [InlineData("published-roads", "name", 0, 8, 0)]
    public void Recipe_RejectsUnsafeSettings(string id, string field, int minZoom, int maxZoom, int maxTiles)
    {
        var recipe = new MetadataV2PairedPublication
        {
            PublicationId = id, IdentityField = field, MinZoom = minZoom, MaxZoom = maxZoom, MaxTiles = maxTiles
        };
        var resource = new MetadataV2Object
        {
            SchemaFields = [new() { Name = "name", Type = MetadataV2FieldType.String }]
        };

        MetadataV2PairedPublicationRules.Problem(recipe, resource).Should().NotBeNull();
    }
}
