// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

/// <summary>Candidate-only surface isolation; excluded from the shared parity filter.</summary>
[Trait("Category", "HostIsolation")]
public sealed class ODataHostIsolationTests(ODataComponentFixture fixture) : IClassFixture<ODataComponentFixture>
{
    [Fact]
    public async Task MinimalHost_DoesNotExposeOtherProtocols()
    {
        fixture.IsExternal.Should().BeFalse("host isolation must run against the minimal composition");
        foreach (var path in new[] { "/rest/services", "/ogc/collections", "/wfs", "/stac", "/mcp" })
        {
            using var response = await fixture.Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }
}
