// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

internal static class ImageServerRasterResourceAccess
{
    public const string ResourceId = "res-image-test";

    public static void Restrict(WebAppFixture fixture, AccessPolicy accessPolicy)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(accessPolicy);

        var graph = fixture.GetCurrentV2GraphSnapshot().Graph;
        graph.Resources.Should().ContainSingle(resource => resource.Metadata.Id == ResourceId);
        var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == ResourceId
                ? resource with { AccessPolicy = accessPolicy }
                : resource).ToArray()
        }, schema: fixture.CurrentSchema);

        fixture.GetCurrentV2GraphSnapshot().Graph.Resources
            .Single(resource => resource.Metadata.Id == ResourceId)
            .AccessPolicy.Should().Be(accessPolicy);
    }
}
