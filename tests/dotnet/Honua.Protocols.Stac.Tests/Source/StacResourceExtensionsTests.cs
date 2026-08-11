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
        var serviceMetadata = new MetadataV2ObjectMetadata { License = "MIT" };

        extensionResource.ResolveLicense(serviceMetadata).Should().Be("CC0-1.0");
        linkResource.ResolveLicense(serviceMetadata).Should().Be("various");
        undeclaredResource.ResolveLicense(serviceMetadata).Should().Be("MIT");
    }
}
