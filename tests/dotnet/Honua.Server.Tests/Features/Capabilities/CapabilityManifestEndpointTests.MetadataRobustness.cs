// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_UnknownMetadataKinds_DoesNotAdvertiseUnsupportedFamilies()
    {
        using var response = await _anonymousClient.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        var packages = document.RootElement.GetProperty("packages");
        var storage = packages.GetProperty("storageFamilies").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        var publications = packages.GetProperty("publicationFamilies").EnumerateArray()
            .Select(value => value.GetString()).ToArray();

        storage.Should().Contain("relational-table");
        publications.Should().Contain("ogc-collection");
        storage.Should().NotContain(value => string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase));
        publications.Should().NotContain(value => string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase));
    }
}
