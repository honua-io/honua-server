// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacResourceExtensionsTests
{
    [UnitTest]
    public void ResolveLicense_PrefersEveryResourceDeclarationBeforeServiceFallback()
    {
        var stacExtension = JsonDocument.Parse("""{ "license": "CC0-1.0" }""").RootElement.Clone();
        var extensionResource = new MetadataV2Resource
        {
            Extensions = new Dictionary<string, JsonElement> { ["stac"] = stacExtension },
        };
        var linkResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Links = [new MetadataV2Link { Rel = "license", Href = "https://example.test/license" }],
            },
        };
        var undeclaredResource = new MetadataV2Resource();
        var serviceMetadata = new MetadataV2ObjectMetadata
        {
            License = "MIT",
            Links =
            [
                new MetadataV2Link { Rel = "license", Href = "https://example.test/service-license" },
                new MetadataV2Link { Rel = "describedby", Href = "https://example.test/service-description" },
            ],
        };

        extensionResource.ResolveLicense(serviceMetadata).Should().Be("CC0-1.0");
        var extensionGovernanceLinks = extensionResource.WithStacServiceGovernanceFallbacks(serviceMetadata).Links;
        extensionGovernanceLinks.Should().NotContain(link =>
            string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase));
        extensionGovernanceLinks.Should().ContainSingle(link =>
            string.Equals(link.Rel, "describedby", StringComparison.OrdinalIgnoreCase));
        linkResource.ResolveLicense(serviceMetadata).Should().Be("various");
        undeclaredResource.ResolveLicense(serviceMetadata).Should().Be("MIT");
    }
}
