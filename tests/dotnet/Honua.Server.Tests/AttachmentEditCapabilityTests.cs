// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests;

/// <summary>
/// Attachment writes edit the feature they belong to, so they follow the publication's declared
/// edit capabilities (SEC-5).
/// </summary>
public sealed class AttachmentEditCapabilityTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(new[] { "Query", "Extract" }, null, true)]
    [InlineData(new[] { "Query" }, new[] { "Query" }, true)]
    [InlineData(new[] { "Query", "Update" }, null, false)]
    [InlineData(new[] { "Query" }, new[] { "Query", "Editing" }, false)]
    [InlineData(null, null, false)]
    public void DeclaresNoEditCapability_FollowsThePublicationsDeclaredCapabilities(
        string[]? serviceCapabilities,
        string[]? publicationCapabilities,
        bool refused)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc", Name = "svc" },
            Options = serviceCapabilities is null
                ? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement> { ["capabilities"] = JsonSerializer.SerializeToElement(serviceCapabilities) }
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub", Name = "pub" },
            ServiceId = "svc",
            ResourceId = "res",
            Capabilities = publicationCapabilities ?? []
        };

        AttachmentEndpoints.DeclaresNoEditCapability(service, publication).Should().Be(refused);
    }
}
