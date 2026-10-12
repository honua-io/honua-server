// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class GeoServicesGovernanceProjectionTests
{
    [Fact]
    public void ProjectLinks_WithStandaloneSpdxLicense_DerivesCanonicalLicenseLink()
    {
        var metadata = new MetadataV2ObjectMetadata { License = "MIT" };

        var links = GeoServicesGovernanceProjection.ProjectLinks(metadata);

        links.Should().ContainSingle().Which.Should().BeEquivalentTo(new GeoServicesGovernanceLink
        {
            Href = "https://spdx.org/licenses/MIT.html",
            Rel = "license",
            Title = "MIT",
        });
    }

    [Fact]
    public void ProjectLinks_WithAuthoredLicenseLink_DoesNotAddDerivedLink()
    {
        var metadata = new MetadataV2ObjectMetadata
        {
            License = "MIT",
            Links =
            [
                new MetadataV2Link
                {
                    Href = "https://example.test/license",
                    Rel = "license",
                },
            ],
        };

        var links = GeoServicesGovernanceProjection.ProjectLinks(metadata);

        links.Should().ContainSingle().Which.Href.Should().Be("https://example.test/license");
    }
}
