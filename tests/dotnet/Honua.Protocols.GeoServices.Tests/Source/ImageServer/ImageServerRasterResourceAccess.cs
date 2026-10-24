// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Sets the access policy on the raster resource the test seed publishes on
/// <see cref="WebAppFixture.TestLayerId"/>.
/// </summary>
/// <remarks>
/// The seed publishes a feature resource and this raster resource on the same storage layer id.
/// <see cref="WebAppFixture.UpdateV2ResourceMetadata"/> updates the first publication at that
/// index, which is the feature resource. The numeric ImageServer route authorizes against the
/// raster resource only (#6452), so a policy on the feature resource does not reach the pixels.
/// </remarks>
internal static class ImageServerRasterResourceAccess
{
    /// <summary>Id of the seeded raster resource published on <see cref="WebAppFixture.TestLayerId"/>.</summary>
    public const string ResourceId = "res-image-test";

    /// <summary>Replaces the seeded raster resource's access policy.</summary>
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
